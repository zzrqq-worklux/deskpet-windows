using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.RAG;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Rag;

/// <summary>
/// Task 1 RAG 契约冒烟测试（Swift VectorStoreProviding.swift 对照）：
/// VectorDocument / VectorSearchResult / IVectorStore 存在性与形状。
/// 行为测试由 T3 负责。
/// </summary>
public class ContractsTests
{
    [Fact]
    public void VectorDocument_ThreeArgConstructor_StillWorks()
    {
        var emb = new[] { 0.1f, 0.2f, 0.3f };
        var doc = new VectorDocument("chunk-1", "文本块", emb);

        Assert.Equal("chunk-1", doc.Id);
        Assert.Equal("文本块", doc.Text);
        Assert.Same(emb, doc.Embedding);
        Assert.Null(doc.Metadata);
    }

    [Fact]
    public void VectorDocument_Metadata_IsOptional()
    {
        var doc = new VectorDocument(
            "chunk-3", "文本", null,
            new Dictionary<string, string>
            {
                ["source"] = "docs/guide.md",
                ["chunkIndex"] = "3",
                ["role"] = "user",
            });

        Assert.NotNull(doc.Metadata);
        Assert.Equal("docs/guide.md", doc.Metadata["source"]);
        Assert.Equal("3", doc.Metadata["chunkIndex"]);
        Assert.Equal("user", doc.Metadata["role"]);
        Assert.Null(doc.Embedding);
    }

    [Fact]
    public void VectorSearchResult_ExposesFields()
    {
        var r = new VectorSearchResult("doc-1", "命中文本", 0.87f);

        Assert.Equal("doc-1", r.DocumentId);
        Assert.Equal("命中文本", r.Text);
        Assert.Equal(0.87f, r.Score);
    }

    [Fact]
    public async Task IVectorStore_IsImplementable()
    {
        var store = new FakeVectorStore();
        var doc = new VectorDocument("d1", "text", null);
        var ct = CancellationToken.None;

        await store.UpsertAsync(new[] { doc }, ct);
        var results = await store.SearchAsync("query", null, 2, ct);
        await store.DeleteAsync(new[] { "d1" }, ct);
        await store.ClearAsync(ct);

        Assert.Single(store.Upserted);
        Assert.Equal(2, store.LastTopK);
        Assert.Equal(new[] { "d1" }, store.Deleted);
        Assert.Equal(1, store.ClearCalls);
        Assert.Single(results);
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public List<VectorDocument> Upserted { get; } = new();
        public int LastTopK { get; private set; }
        public List<string> Deleted { get; } = new();
        public int ClearCalls { get; private set; }

        public Task UpsertAsync(IReadOnlyList<VectorDocument> docs, CancellationToken ct)
        {
            Upserted.AddRange(docs);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            string query, float[]? queryEmbedding, int topK, CancellationToken ct)
        {
            LastTopK = topK;
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                new[] { new VectorSearchResult("d1", "text", 0.9f) });
        }

        public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            Deleted.AddRange(ids);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct)
        {
            ClearCalls++;
            return Task.CompletedTask;
        }
    }
}
