namespace DeskPet.Core.RAG;

/// <summary>RAG 导入错误类别，对应 Swift <c>RAGError</c>（RAGManager.swift 70-82）各 case。</summary>
public enum RagErrorKind
{
    /// <summary>文件没有可导入的文本内容（Swift <c>.emptyFile</c>）。</summary>
    EmptyFile,

    /// <summary>文件不是可读的文本文件，仅支持 UTF-8/UTF-16（Swift <c>.unreadableFile</c>）。</summary>
    UnreadableFile,
}

/// <summary>
/// RAG 导入异常，消息文案与 Swift <c>RAGError.errorDescription</c> 一一对应。
/// <see cref="Kind"/> 供 UI 按类别处理。
/// </summary>
public sealed class RagException : Exception
{
    /// <summary>错误类别（对应 Swift <c>RAGError</c> case）。</summary>
    public RagErrorKind Kind { get; }

    public RagException(RagErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    /// <summary>文件为空/只有空白，无可导入内容（Swift <c>.emptyFile</c>）。</summary>
    public static RagException EmptyFile(string name) =>
        new(RagErrorKind.EmptyFile, $"「{name}」没有可导入的文本内容");

    /// <summary>UTF-8 与 UTF-16 均无法解码（Swift <c>.unreadableFile</c>）。</summary>
    public static RagException UnreadableFile(string name) =>
        new(RagErrorKind.UnreadableFile, $"「{name}」不是可读的文本文件（支持 UTF-8/UTF-16）");
}
