using System.Text.Json;

namespace DeskPet.Core.RAG;

/// <summary>
/// 本地 JSON 向量库（Swift <c>LocalVectorStore.swift</c> 移植）：
/// 分块 + embedding 持久化到 <c>&lt;dataDirectory&gt;/vectorstore.json</c>（Core 不写死 %APPDATA%，
/// 数据目录由构造注入），检索为暴力余弦相似度 + topK 截断；所有读写线程安全（lock），
/// 每次变更后落盘，加载时文件缺失/损坏按空库处理。
/// </summary>
public sealed class LocalVectorStore : IVectorStore
{
    /// <summary>存储文件名（Swift <c>vectorstore.json</c>）。</summary>
    public const string StoreFileName = "vectorstore.json";

    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly List<VectorDocument> _chunks = new();

    /// <param name="dataDirectory">持久化目录（应用层传 %APPDATA%/DeskPet；测试传临时目录）。</param>
    public LocalVectorStore(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, StoreFileName);
        Load();
    }

    /// <summary>全部已存分块快照（Swift <c>chunks</c>，设置页展示分块数用）。</summary>
    public IReadOnlyList<VectorDocument> Chunks
    {
        get { lock (_lock) { return _chunks.ToList(); } }
    }

    /// <summary>批量写入/更新：按 id 覆盖或追加，随后落盘（Swift <c>upsert</c> 38-47）。</summary>
    public Task UpsertAsync(IReadOnlyList<VectorDocument> docs, CancellationToken ct)
    {
        lock (_lock)
        {
            foreach (var doc in docs)
            {
                var index = _chunks.FindIndex(c => c.Id == doc.Id);
                if (index >= 0) _chunks[index] = doc;
                else _chunks.Add(doc);
            }
            Persist();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 语义检索：query embedding 为空返回空；只对带 embedding 的分块计算余弦相似度，
    /// 按分数降序后取 <c>prefix(max(1, topK))</c> 条（Swift <c>search</c> 49-64）。
    /// </summary>
    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query, float[]? queryEmbedding, int topK, CancellationToken ct)
    {
        lock (_lock)
        {
            if (queryEmbedding is null || queryEmbedding.Length == 0)
            {
                return Task.FromResult<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>());
            }

            var results = _chunks
                .Where(c => c.Embedding is not null)
                .Select(c => new VectorSearchResult(c.Id, c.Text, CosineSimilarity(queryEmbedding, c.Embedding!)))
                .OrderByDescending(r => r.Score)
                .Take(Math.Max(1, topK))
                .ToList();
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
        }
    }

    /// <summary>删除指定 id 的分块并落盘（Swift <c>delete(ids:)</c> 66-70）。</summary>
    public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        lock (_lock)
        {
            var idSet = ids.ToHashSet();
            _chunks.RemoveAll(c => idSet.Contains(c.Id));
            Persist();
        }
        return Task.CompletedTask;
    }

    /// <summary>清空所有分块并落盘（Swift <c>clear()</c> 72-75）。</summary>
    public Task ClearAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _chunks.Clear();
            Persist();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 余弦相似度（Swift <c>cosineSimilarity</c> 109-121 公式逐行移植）：
    /// 取两向量较短长度做点积与模长累加；任一模长为 0 返回 0。
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0.0;
        var normA = 0.0;
        var normB = 0.0;
        var count = Math.Min(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA <= 0 || normB <= 0) return 0f;
        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<VectorDocument>>(json);
            _chunks.Clear();
            if (loaded is not null) _chunks.AddRange(loaded);
        }
        catch (JsonException)
        {
            _chunks.Clear();
        }
        catch (IOException)
        {
            _chunks.Clear();
        }
    }

    /// <summary>原子落盘（临时文件 + 覆盖移动，Swift <c>persist</c> 102-105 的 .atomic 语义）。</summary>
    private void Persist()
    {
        var json = JsonSerializer.Serialize(_chunks);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
    }
}
