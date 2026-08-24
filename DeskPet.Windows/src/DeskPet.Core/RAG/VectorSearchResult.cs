namespace DeskPet.Core.RAG;

/// <summary>
/// 语义检索结果，对应 Swift <c>VectorSearchResult</c>
/// （Score 为 0-1 相似度，越高越相关；由具体实现定义）。
/// </summary>
public record VectorSearchResult(string DocumentId, string Text, float Score);
