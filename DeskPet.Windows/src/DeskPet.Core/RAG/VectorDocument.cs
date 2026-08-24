namespace DeskPet.Core.RAG;

/// <summary>
/// 向量库中的一条文档（文本块 + 可选元数据 + 可选 embedding），
/// 对应 Swift <c>VectorDocument</c>。
/// 元数据（Metadata）为 Swift 的 <c>source/sourcePath/chunkIndex/role</c> 等
/// 可移植条目，用于重导入/按来源删除（SDD Ruling 2）。
/// </summary>
public record VectorDocument(
    string Id,
    string Text,
    float[]? Embedding = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
