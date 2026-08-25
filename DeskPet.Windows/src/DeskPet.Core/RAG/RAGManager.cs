using System.Text;
using System.Text.Json;
using DeskPet.Core.Chat;

namespace DeskPet.Core.RAG;

/// <summary>
/// RAG 数据流编排（Swift <c>RAGManager.swift</c> 移植）：导入文档 → 分块 →
/// 批量生成向量 → 写入向量库；维护文档注册表（Swift <c>RAGDocument</c>，
/// SDD Ruling 6 追加按文档跟踪分块 id）并持久化为 JSON，重启后恢复。
/// 对话侧检索入口 <see cref="SearchAsync"/>。
/// </summary>
public sealed class RAGManager
{
    /// <summary>分块大小默认值（Swift <c>Defaults[.petRAGChunkSize]</c> 500，设置页可后续绑定）。</summary>
    public const int DefaultChunkSize = 500;

    /// <summary>分块重叠默认值（Swift <c>Defaults[.petRAGChunkOverlap]</c> 100）。</summary>
    public const int DefaultChunkOverlap = 100;

    /// <summary>检索 topK 默认值（Swift <c>VectorStoreManager.search</c> 默认 3）。</summary>
    public const int DefaultTopK = 3;

    /// <summary>单批 embedding 条数（Swift <c>batched(by: 16)</c>）。</summary>
    private const int EmbeddingBatchSize = 16;

    /// <summary>注册表文件名（Swift <c>Defaults[.petRAGDocuments]</c> 语义 → JSON 文件）。</summary>
    public const string RegistryFileName = "rag_documents.json";

    /// <summary>检索类文档角色（Swift <c>RAGDocumentRole.retrieval.rawValue</c>）。</summary>
    private const string RetrievalRole = "Retrieval";

    private readonly IEmbeddingService _embedding;
    private readonly IVectorStore _store;
    private readonly string _registryPath;
    private readonly object _lock = new();
    private readonly List<RAGDocument> _documents = new();
    private readonly SemaphoreSlim _importGate = new(1, 1);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);

    /// <param name="embedding">向量化服务（PetChatClient；EmbeddingModel 由它自行从设置读取）。</param>
    /// <param name="store">向量库（LocalVectorStore）。</param>
    /// <param name="dataDirectory">注册表持久化目录（应用层传 %APPDATA%/DeskPet；测试传临时目录）。</param>
    public RAGManager(IEmbeddingService embedding, IVectorStore store, string dataDirectory)
    {
        _embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _registryPath = Path.Combine(dataDirectory, RegistryFileName);
        _documents.AddRange(LoadRegistry());
    }

    /// <summary>已导入文档注册表快照（Swift <c>documents</c>；设置页展示/删除用）。</summary>
    public IReadOnlyList<RAGDocument> Documents
    {
        get { lock (_lock) { return _documents.ToList(); } }
    }

    /// <summary>
    /// 导入一个文件：读取（UTF-8 → UTF-16 兜底，均失败抛 <see cref="RagException.UnreadableFile"/>）
    /// → 分块（500/100）→ 空内容抛 <see cref="RagException.EmptyFile"/> →
    /// 若注册表已有同源（sourcePath）文档先删除其旧分块 → 每批 16 条生成向量 →
    /// 写入向量库（元数据 source/sourcePath/chunkIndex/role）→ 更新注册表并持久化。
    /// 导入串行化（SemaphoreSlim，对应 Swift @MainActor 语义）。
    /// </summary>
    public async Task ImportAsync(string sourcePath, string sourceName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceName);

        await _importGate.WaitAsync(ct);
        try
        {
            var text = ReadText(sourcePath, sourceName);
            var chunks = DocumentChunker.Chunk(text, DefaultChunkSize, DefaultChunkOverlap);
            if (chunks.Count == 0) throw RagException.EmptyFile(sourceName);

            RAGDocument? existing;
            lock (_lock) { existing = _documents.FirstOrDefault(d => d.SourcePath == sourcePath); }
            // 覆盖重导同名文件：先删除旧分块（SDD Ruling 6：按注册表跟踪的 id 精确删除）。
            if (existing is not null) await _store.DeleteAsync(existing.ChunkIds, ct);

            var vectorDocs = new List<VectorDocument>();
            var chunkIds = new List<string>();
            for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
            {
                var batch = chunks.Skip(offset).Take(EmbeddingBatchSize).ToList();
                var embeddings = await _embedding.EmbedAsync(batch, ct);
                for (var i = 0; i < batch.Count; i++)
                {
                    var id = Guid.NewGuid().ToString("D");
                    chunkIds.Add(id);
                    vectorDocs.Add(new VectorDocument(
                        id,
                        batch[i],
                        embeddings[i],
                        new Dictionary<string, string>
                        {
                            ["source"] = sourceName,
                            ["sourcePath"] = sourcePath,
                            ["chunkIndex"] = (offset + i).ToString(),
                            ["role"] = RetrievalRole,
                        }));
                }
            }

            await _store.UpsertAsync(vectorDocs, ct);
            UpdateRegistry(sourcePath, sourceName, chunkIds);
        }
        finally
        {
            _importGate.Release();
        }
    }

    /// <summary>
    /// 删除一个文档：按注册表跟踪的分块 id 清理向量库，移除注册表条目并持久化（Swift <c>removeDocument</c> 189-194）。
    /// 与导入共用 <c>_importGate</c> 串行化（最终评审 I2：否则可插入导入中段，导入收尾会复活文档）。
    /// </summary>
    public async Task RemoveAsync(string documentId, CancellationToken ct = default)
    {
        await _importGate.WaitAsync(ct);
        try
        {
            RAGDocument? document;
            lock (_lock) { document = _documents.FirstOrDefault(d => d.Id == documentId); }
            if (document is null) return;

            await _store.DeleteAsync(document.ChunkIds, ct);
            lock (_lock)
            {
                _documents.Remove(document);
                PersistRegistry();
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    /// <summary>清空向量库与注册表并持久化（Swift <c>clearAll</c> 196-200）。与导入共用 <c>_importGate</c> 串行化（最终评审 I2）。</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _importGate.WaitAsync(ct);
        try
        {
            await _store.ClearAsync(ct);
            lock (_lock)
            {
                _documents.Clear();
                PersistRegistry();
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    /// <summary>
    /// 语义检索：query 为空（空白）直接返回空、不调 embedding；
    /// 否则先对 query 生成向量再交给向量库检索（Swift <c>VectorStoreManager.search</c> 语义，默认 topK 3）。
    /// </summary>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query, int topK = DefaultTopK, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<VectorSearchResult>();

        var embeddings = await _embedding.EmbedAsync(new[] { query }, ct);
        if (embeddings.Count == 0) return Array.Empty<VectorSearchResult>();

        return await _store.SearchAsync(query, embeddings[0], topK, ct);
    }

    /// <summary>
    /// 读取文件文本：UTF-8 优先，失败回退 UTF-16，两者均失败抛 UnreadableFile（Swift <c>readText</c> 204-213）。
    /// 解码后剥离开头 BOM 字符（U+FEFF，Notepad 等编辑器保存的 UTF-8/UTF-16 文件常带 BOM）。
    /// </summary>
    private static string ReadText(string sourcePath, string sourceName)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                text = StrictUtf16.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw RagException.UnreadableFile(sourceName);
            }
        }
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>按 sourcePath 更新或追加注册表条目并持久化（Swift <c>updateRegistry</c> 215-235）。</summary>
    private void UpdateRegistry(string sourcePath, string sourceName, IReadOnlyList<string> chunkIds)
    {
        lock (_lock)
        {
            var existing = _documents.FirstOrDefault(d => d.SourcePath == sourcePath);
            if (existing is not null)
            {
                existing.SourceName = sourceName;
                existing.ChunkCount = chunkIds.Count;
                existing.ImportedAt = DateTime.UtcNow;
                existing.ChunkIds.Clear();
                existing.ChunkIds.AddRange(chunkIds);
            }
            else
            {
                _documents.Add(new RAGDocument
                {
                    Id = Guid.NewGuid().ToString("D"),
                    SourcePath = sourcePath,
                    SourceName = sourceName,
                    ChunkCount = chunkIds.Count,
                    ImportedAt = DateTime.UtcNow,
                    ChunkIds = chunkIds.ToList(),
                });
            }
            PersistRegistry();
        }
    }

    private List<RAGDocument> LoadRegistry()
    {
        try
        {
            if (!File.Exists(_registryPath)) return new List<RAGDocument>();
            return JsonSerializer.Deserialize<List<RAGDocument>>(File.ReadAllText(_registryPath))
                ?? new List<RAGDocument>();
        }
        catch (JsonException)
        {
            return new List<RAGDocument>();
        }
        catch (IOException)
        {
            return new List<RAGDocument>();
        }
    }

    private void PersistRegistry()
    {
        File.WriteAllText(_registryPath, JsonSerializer.Serialize(_documents));
    }
}
