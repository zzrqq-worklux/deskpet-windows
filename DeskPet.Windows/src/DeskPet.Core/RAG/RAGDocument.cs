namespace DeskPet.Core.RAG;

/// <summary>
/// 已导入文档的注册表条目，对应 Swift <c>RAGDocument</c>（RAGManager.swift 28-61，
/// id/sourcePath/sourceName/chunkCount/importedAt 形状），
/// 另按 SDD Ruling 6 跟踪该文档的全部分块 id（<see cref="ChunkIds"/>），
/// 供覆盖重导/删除时按 id 精确清理向量库。
/// 可序列化为 JSON 持久化到数据目录，重启后恢复（设置页展示用，Task 6）。
/// </summary>
public sealed class RAGDocument
{
    /// <summary>文档 id（对应 Swift <c>id: UUID</c>）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>源文件完整路径（对应 Swift <c>sourcePath</c>，注册表按此匹配覆盖重导）。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>源文件名（对应 Swift <c>sourceName</c>，也写入分块元数据 <c>source</c>）。</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>分块总数（对应 Swift <c>chunkCount</c>）。</summary>
    public int ChunkCount { get; set; }

    /// <summary>导入时间（对应 Swift <c>importedAt: Date</c>）。</summary>
    public DateTime ImportedAt { get; set; }

    /// <summary>该文档在当前向量库中的全部分块 id（SDD Ruling 6，JSON 序列化）。</summary>
    public List<string> ChunkIds { get; set; } = new();
}
