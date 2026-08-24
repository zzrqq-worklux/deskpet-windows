namespace DeskPet.Core.RAG;

/// <summary>
/// 向量库统一接口，对应 Swift <c>VectorStoreProviding</c> 协议
/// （upsert / search / delete / clear）。
/// </summary>
public interface IVectorStore
{
    /// <summary>批量写入/更新文档（对应 Swift <c>upsert(_:)</c>）。</summary>
    Task UpsertAsync(IReadOnlyList<VectorDocument> docs, CancellationToken ct);

    /// <summary>语义检索：按查询文本和/或 query embedding 返回最相关的 topK 条（对应 Swift <c>search</c>）。</summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query, float[]? queryEmbedding, int topK, CancellationToken ct);

    /// <summary>删除指定 id 的文档（对应 Swift <c>delete(ids:)</c>）。</summary>
    Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>清空所有文档（对应 Swift <c>clear()</c>）。</summary>
    Task ClearAsync(CancellationToken ct);
}
