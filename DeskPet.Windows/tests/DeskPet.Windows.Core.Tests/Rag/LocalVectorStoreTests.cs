using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.RAG;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Rag;

/// <summary>
/// Task 3 LocalVectorStore 行为测试（对照 Swift LocalVectorStore.swift）：
/// 余弦相似度排序、topK 前缀语义、upsert 覆盖、删除/清空、JSON 跨实例持久化、
/// 损坏/缺失文件容错、并发安全。
/// </summary>
public class LocalVectorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetTests", Guid.NewGuid().ToString("N"));

    private LocalVectorStore CreateStore() => new(_dir);

    private static VectorDocument Doc(string id, string text, float[]? embedding = null, IReadOnlyDictionary<string, string>? metadata = null)
        => new(id, text, embedding, metadata);

    [Fact]
    public async Task Search_ReturnsResultsSortedByCosineSimilarityDesc()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[]
        {
            Doc("a", "apple", new[] { 1f, 0f, 0f }),
            Doc("b", "banana", new[] { 0f, 1f, 0f }),
            Doc("c", "apple banana", new[] { 0.7071068f, 0.7071068f, 0f }),
            Doc("d", "no vector here", null),
        }, CancellationToken.None);

        var results = await store.SearchAsync("apple", new[] { 1f, 0f, 0f }, 10, CancellationToken.None);

        Assert.Equal(new[] { "a", "c", "b" }, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.0, results[0].Score, 4);
        Assert.Equal(0.7071, results[1].Score, 4);
        Assert.Equal(0.0, results[2].Score, 4);
    }

    [Fact]
    public async Task Search_ChunksWithoutEmbedding_AreExcluded()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[]
        {
            Doc("with", "has vector", new[] { 1f }),
            Doc("without", "no vector", null),
        }, CancellationToken.None);

        var results = await store.SearchAsync("q", new[] { 1f }, 10, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("with", results[0].DocumentId);
    }

    [Fact]
    public async Task Search_TopK_TruncatesAndNeverReturnsZero()
    {
        var store = CreateStore();
        var docs = Enumerable.Range(0, 5)
            .Select(i => Doc($"d{i}", $"text {i}", new[] { 1f }))
            .ToList();
        await store.UpsertAsync(docs, CancellationToken.None);

        var truncated = await store.SearchAsync("q", new[] { 1f }, 2, CancellationToken.None);
        var zeroTopK = await store.SearchAsync("q", new[] { 1f }, 0, CancellationToken.None);

        Assert.Equal(2, truncated.Count);
        Assert.Single(zeroTopK);
    }

    [Fact]
    public async Task Search_NullOrEmptyQueryEmbedding_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[] { Doc("a", "text", new[] { 1f }) }, CancellationToken.None);

        Assert.Empty(await store.SearchAsync("q", null, 10, CancellationToken.None));
        Assert.Empty(await store.SearchAsync("q", Array.Empty<float>(), 10, CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_SameId_ReplacesExisting()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[] { Doc("x", "old text", new[] { 1f }) }, CancellationToken.None);
        await store.UpsertAsync(new[] { Doc("x", "new text", new[] { 2f }) }, CancellationToken.None);

        Assert.Single(store.Chunks);
        Assert.Equal("new text", store.Chunks[0].Text);
        var results = await store.SearchAsync("q", new[] { 1f }, 10, CancellationToken.None);
        Assert.Equal("new text", results[0].Text);
    }

    [Fact]
    public async Task Delete_RemovesOnlyListedIds()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[]
        {
            Doc("a", "one", new[] { 1f }),
            Doc("b", "two", new[] { 1f }),
            Doc("c", "three", new[] { 1f }),
        }, CancellationToken.None);

        await store.DeleteAsync(new[] { "a", "c" }, CancellationToken.None);

        Assert.Equal(new[] { "b" }, store.Chunks.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task Clear_RemovesAll()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[] { Doc("a", "one", new[] { 1f }) }, CancellationToken.None);

        await store.ClearAsync(CancellationToken.None);

        Assert.Empty(store.Chunks);
    }

    [Fact]
    public async Task Upsert_PersistsAcrossInstances_WithMetadataAndEmbedding()
    {
        var metadata = new Dictionary<string, string> { ["source"] = "guide.md", ["role"] = "Retrieval" };
        await CreateStore().UpsertAsync(new[]
        {
            Doc("a", "hello world", new[] { 1f, 0f }, metadata),
            Doc("b", "no embedding", null),
        }, CancellationToken.None);

        var reloaded = CreateStore();

        Assert.Equal(2, reloaded.Chunks.Count);
        Assert.Equal("hello world", reloaded.Chunks[0].Text);
        Assert.Equal(new[] { 1f, 0f }, reloaded.Chunks[0].Embedding);
        Assert.Equal("guide.md", reloaded.Chunks[0].Metadata!["source"]);
        Assert.Null(reloaded.Chunks[1].Embedding);
        var results = await reloaded.SearchAsync("q", new[] { 1f, 0f }, 10, CancellationToken.None);
        Assert.Equal("a", results[0].DocumentId);
    }

    [Fact]
    public async Task DeleteAndClear_PersistAcrossInstances()
    {
        var store = CreateStore();
        await store.UpsertAsync(new[]
        {
            Doc("a", "one", new[] { 1f }),
            Doc("b", "two", new[] { 1f }),
        }, CancellationToken.None);

        await store.DeleteAsync(new[] { "a" }, CancellationToken.None);
        Assert.Single(CreateStore().Chunks);

        await store.ClearAsync(CancellationToken.None);
        Assert.Empty(CreateStore().Chunks);
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        var store = CreateStore();

        Assert.Empty(store.Chunks);
    }

    [Fact]
    public void Load_CorruptFile_StartsEmptyWithoutThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "vectorstore.json"), "not json {{{");

        var store = CreateStore();

        Assert.Empty(store.Chunks);
    }

    [Fact]
    public async Task ConcurrentUpsertsAndSearches_AreConsistent()
    {
        var store = CreateStore();
        var ct = CancellationToken.None;

        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                await store.UpsertAsync(new[] { Doc($"w{worker}-{i}", $"text {worker}-{i}", new[] { 1f }) }, ct);
                await store.SearchAsync("q", new[] { 1f }, 3, ct);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(200, store.Chunks.Count);
    }

    [Fact]
    public void CosineSimilarity_MatchesSwiftFormula()
    {
        Assert.Equal(1.0, LocalVectorStore.CosineSimilarity(new[] { 1f, 2f, 3f }, new[] { 1f, 2f, 3f }), 6);
        Assert.Equal(0.0, LocalVectorStore.CosineSimilarity(new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }), 6);
        Assert.Equal(0.0, LocalVectorStore.CosineSimilarity(new[] { 0f, 0f }, new[] { 1f, 0f }), 6);
        Assert.Equal(0.7071, LocalVectorStore.CosineSimilarity(new[] { 1f, 0f, 0f }, new[] { 0.7071068f, 0.7071068f, 0f }), 4);
        Assert.Equal(1.0, LocalVectorStore.CosineSimilarity(new[] { 1f, 1f }, new[] { 1f, 1f, 1f }), 6);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
