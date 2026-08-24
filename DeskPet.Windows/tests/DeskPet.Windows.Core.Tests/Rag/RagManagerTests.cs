using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Chat;
using DeskPet.Core.RAG;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Rag;

/// <summary>
/// Task 3 RAGManager 数据流测试（对照 Swift RAGManager.swift importFiles 102-185 /
/// removeDocument / clearAll / updateRegistry）：
/// 导入→检索 round-trip、分块批次(16)、元数据、覆盖重导删除旧分块、删除/清空、
/// 注册表 JSON 跨实例持久化、空文件/不可读文件错误、UTF-16 读取、空查询早退。
/// </summary>
public class RagManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetTests", Guid.NewGuid().ToString("N"));

    private (RAGManager Manager, LocalVectorStore Store, KeywordEmbeddingService Embedding) CreateWorld()
    {
        var embedding = new KeywordEmbeddingService();
        var store = CreateStore();
        return (new RAGManager(embedding, store, _dir), store, embedding);
    }

    private LocalVectorStore CreateStore() => new(_dir);

    private static string WriteFile(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Import_ThenSearch_FindsRelevantChunk()
    {
        var (manager, store, _) = CreateWorld();
        var path = WriteFile(_dir, "guide.md", "hello world, this is a deskpet rag document.\nThe second line has more content.");

        await manager.ImportAsync(path, "guide.md", CancellationToken.None);

        var doc = Assert.Single(manager.Documents);
        Assert.Equal("guide.md", doc.SourceName);
        Assert.Equal(path, doc.SourcePath);
        Assert.Equal(1, doc.ChunkCount);
        Assert.Single(doc.ChunkIds);
        Assert.Single(store.Chunks);

        var results = await manager.SearchAsync("hello", 1, CancellationToken.None);

        var hit = Assert.Single(results);
        Assert.Contains("hello", hit.Text);
        Assert.Equal(doc.ChunkIds[0], hit.DocumentId);
        Assert.Equal(0.5, hit.Score, 4);
    }

    [Fact]
    public async Task Import_SetsSwiftParityMetadata()
    {
        var (manager, store, _) = CreateWorld();
        var path = WriteFile(_dir, "notes.txt", "first chunk content here");

        await manager.ImportAsync(path, "notes.txt", CancellationToken.None);

        var chunk = Assert.Single(store.Chunks);
        Assert.Equal("notes.txt", chunk.Metadata!["source"]);
        Assert.Equal(path, chunk.Metadata["sourcePath"]);
        Assert.Equal("0", chunk.Metadata["chunkIndex"]);
        Assert.Equal("Retrieval", chunk.Metadata["role"]);
        Assert.NotNull(chunk.Embedding);
    }

    [Fact]
    public async Task Import_EmptyFile_ThrowsEmptyFileError()
    {
        var (manager, _, _) = CreateWorld();
        var path = WriteFile(_dir, "blank.txt", "   \n\n\t ");

        var ex = await Assert.ThrowsAsync<RagException>(
            () => manager.ImportAsync(path, "blank.txt", CancellationToken.None));

        Assert.Equal(RagErrorKind.EmptyFile, ex.Kind);
        Assert.Equal("「blank.txt」没有可导入的文本内容", ex.Message);
    }

    [Fact]
    public async Task Import_ZeroByteFile_ThrowsEmptyFileError()
    {
        var (manager, _, _) = CreateWorld();
        var path = Path.Combine(_dir, "empty.txt");
        File.WriteAllText(path, "");

        var ex = await Assert.ThrowsAsync<RagException>(
            () => manager.ImportAsync(path, "empty.txt", CancellationToken.None));

        Assert.Equal(RagErrorKind.EmptyFile, ex.Kind);
    }

    [Fact]
    public async Task Import_UnreadableFile_ThrowsUnreadableFileError()
    {
        var (manager, _, _) = CreateWorld();
        var path = Path.Combine(_dir, "binary.dat");
        File.WriteAllBytes(path, new byte[] { 0xC3, 0x28, 0x00 });

        var ex = await Assert.ThrowsAsync<RagException>(
            () => manager.ImportAsync(path, "binary.dat", CancellationToken.None));

        Assert.Equal(RagErrorKind.UnreadableFile, ex.Kind);
        Assert.Equal("「binary.dat」不是可读的文本文件（支持 UTF-8/UTF-16）", ex.Message);
    }

    [Fact]
    public async Task Import_MissingFile_PropagatesFileNotFound()
    {
        var (manager, _, _) = CreateWorld();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => manager.ImportAsync(Path.Combine(_dir, "nope.md"), "nope.md", CancellationToken.None));
    }

    [Fact]
    public async Task Import_EmbedsInBatchesOfSixteen()
    {
        var (manager, store, embedding) = CreateWorld();
        var text = string.Concat(Enumerable.Repeat("hello ", 2000));
        var path = WriteFile(_dir, "big.txt", text);

        await manager.ImportAsync(path, "big.txt", CancellationToken.None);

        Assert.Equal(30, store.Chunks.Count);
        Assert.Equal(new[] { 16, 14 }, embedding.BatchSizes);
        Assert.All(store.Chunks, c => Assert.NotNull(c.Embedding));
        Assert.Equal(30, Assert.Single(manager.Documents).ChunkCount);
    }

    [Fact]
    public async Task Import_SameSource_DeletesOldChunksBeforeReimport()
    {
        var (manager, store, _) = CreateWorld();
        var path = WriteFile(_dir, "doc.txt", new string('a', 1500));
        await manager.ImportAsync(path, "doc.txt", CancellationToken.None);
        Assert.Equal(4, store.Chunks.Count);

        File.WriteAllText(path, new string('b', 400));
        await manager.ImportAsync(path, "doc.txt", CancellationToken.None);

        Assert.Single(store.Chunks);
        Assert.Equal(new string('b', 400), store.Chunks[0].Text);
        var doc = Assert.Single(manager.Documents);
        Assert.Equal(1, doc.ChunkCount);
        Assert.Single(doc.ChunkIds);
        Assert.Equal(doc.ChunkIds[0], store.Chunks[0].Id);
    }

    [Fact]
    public async Task Remove_Document_DeletesItsChunksAndRegistryEntry()
    {
        var (manager, store, _) = CreateWorld();
        var path1 = WriteFile(_dir, "a.md", "hello world deskpet rag");
        var path2 = WriteFile(_dir, "b.md", new string('x', 5000) + "hello");
        await manager.ImportAsync(path1, "a.md", CancellationToken.None);
        await manager.ImportAsync(path2, "b.md", CancellationToken.None);
        var first = manager.Documents.First(d => d.SourceName == "a.md");
        var before = store.Chunks.Count;

        await manager.RemoveAsync(first.Id, CancellationToken.None);

        Assert.Equal(before - 1, store.Chunks.Count);
        Assert.Single(manager.Documents);
        Assert.DoesNotContain(store.Chunks, c => c.Metadata?["source"] == "a.md");
    }

    [Fact]
    public async Task Clear_EmptiesStoreAndRegistry_AcrossInstances()
    {
        var (manager, store, _) = CreateWorld();
        await manager.ImportAsync(WriteFile(_dir, "a.md", "hello world"), "a.md", CancellationToken.None);
        await manager.ImportAsync(WriteFile(_dir, "b.md", "deskpet rag"), "b.md", CancellationToken.None);

        await manager.ClearAsync(CancellationToken.None);

        Assert.Empty(store.Chunks);
        Assert.Empty(manager.Documents);
        Assert.Empty(CreateStore().Chunks);
        Assert.Empty(new RAGManager(new KeywordEmbeddingService(), CreateStore(), _dir).Documents);
    }

    [Fact]
    public async Task Registry_PersistsAcrossInstances()
    {
        var embedding = new KeywordEmbeddingService();
        var manager1 = new RAGManager(embedding, CreateStore(), _dir);
        var path = WriteFile(_dir, "persist.md", "hello world, persisted registry content.");
        await manager1.ImportAsync(path, "persist.md", CancellationToken.None);

        var store2 = CreateStore();
        var manager2 = new RAGManager(new KeywordEmbeddingService(), store2, _dir);

        var doc = Assert.Single(manager2.Documents);
        Assert.Equal("persist.md", doc.SourceName);
        Assert.Equal(path, doc.SourcePath);
        Assert.Equal(1, doc.ChunkCount);
        Assert.Single(doc.ChunkIds);
        Assert.Single(store2.Chunks);
        Assert.Single(await manager2.SearchAsync("hello", 1, CancellationToken.None));
    }

    [Fact]
    public async Task Import_Utf16File_ReadsAndChunksCorrectly()
    {
        var (manager, store, _) = CreateWorld();
        var text = "中文测试内容，桌宠知识库文档。";
        var path = Path.Combine(_dir, "utf16.md");
        File.WriteAllText(path, text, Encoding.Unicode);

        await manager.ImportAsync(path, "utf16.md", CancellationToken.None);

        Assert.Equal(text, Assert.Single(store.Chunks).Text);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyWithoutEmbeddingCall()
    {
        var embedding = new KeywordEmbeddingService();
        var manager = new RAGManager(embedding, CreateStore(), _dir);
        var path = WriteFile(_dir, "a.md", "hello world");
        await manager.ImportAsync(path, "a.md", CancellationToken.None);
        var callsAfterImport = embedding.CallCount;

        var results = await manager.SearchAsync("   ", 3, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(callsAfterImport, embedding.CallCount);
    }

    [Fact]
    public async Task Search_EmbeddingReturnsEmpty_ReturnsEmptyResults()
    {
        var emptyEmbedding = new EmptyEmbeddingService();
        var manager = new RAGManager(emptyEmbedding, CreateStore(), _dir);

        var results = await manager.SearchAsync("hello", 3, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(1, emptyEmbedding.CallCount);
    }

    [Fact]
    public async Task Search_TopK_DefaultsToThree()
    {
        var (manager, _, _) = CreateWorld();
        var text = string.Concat(Enumerable.Repeat("hello ", 600));
        await manager.ImportAsync(WriteFile(_dir, "many.md", text), "many.md", CancellationToken.None);

        var results = await manager.SearchAsync("hello");

        Assert.Equal(3, results.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>关键字词袋 embedding：文本包含 hello/world/deskpet/rag 时对应分量置 1（确定性、可解释）。</summary>
    private sealed class KeywordEmbeddingService : IEmbeddingService
    {
        public List<int> BatchSizes { get; } = new();
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            CallCount++;
            BatchSizes.Add(texts.Count);
            var result = new List<float[]>(texts.Count);
            foreach (var text in texts)
            {
                result.Add(new[]
                {
                    text.Contains("hello") ? 1f : 0f,
                    text.Contains("world") ? 1f : 0f,
                    text.Contains("deskpet") ? 1f : 0f,
                    text.Contains("rag") ? 1f : 0f,
                });
            }
            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }
    }

    /// <summary>始终返回空向量列表（模拟 embedding 服务无输出）。</summary>
    private sealed class EmptyEmbeddingService : IEmbeddingService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
        }
    }
}
