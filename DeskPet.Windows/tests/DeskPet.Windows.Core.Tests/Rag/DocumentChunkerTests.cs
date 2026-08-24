using System;
using System.Linq;
using DeskPet.Core.RAG;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Rag;

/// <summary>
/// Task 3 DocumentChunker 行为测试（对照 Swift RAGManager.chunkText 244-277）：
/// 字符窗口 + 重叠、优先在换行处断开（仅 half-window 之后）、\r\n 归一化、
/// 空白块跳过、min-chunk-size 下限。
/// </summary>
public class DocumentChunkerTests
{
    [Fact]
    public void Chunk_ShortText_ReturnsSinglePiece()
    {
        var pieces = DocumentChunker.Chunk("hello world", 500, 100);

        Assert.Single(pieces);
        Assert.Equal("hello world", pieces[0]);
    }

    [Fact]
    public void Chunk_EmptyOrWhitespaceText_ReturnsEmpty()
    {
        Assert.Empty(DocumentChunker.Chunk("", 500, 100));
        Assert.Empty(DocumentChunker.Chunk("  \n\t \r\n ", 500, 100));
    }

    [Fact]
    public void Chunk_NullText_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentChunker.Chunk(null!, 500, 100));
    }

    [Fact]
    public void Chunk_SizeBelowFloor_UsesHundredAsMinimum()
    {
        var pieces = DocumentChunker.Chunk(new string('a', 150), 10, 0);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(100, pieces[0].Length);
        Assert.Equal(50, pieces[1].Length);
    }

    [Fact]
    public void Chunk_Overlap_ReusesTailOfPreviousPiece()
    {
        var pieces = DocumentChunker.Chunk(new string('a', 160), 100, 50);

        Assert.Equal(new[] { 100, 100, 60 }, pieces.Select(p => p.Length).ToArray());
        Assert.StartsWith(new string('a', 50), pieces[1]);
        Assert.EndsWith(new string('a', 10), pieces[1]);
        Assert.Equal(new string('a', 160), string.Concat(pieces[0], pieces[2]));
    }

    [Fact]
    public void Chunk_NewlineInsideWindow_BreaksAtNewline()
    {
        var pieces = DocumentChunker.Chunk(new string('a', 60) + "\n" + new string('b', 60), 100, 10);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(new string('a', 60), pieces[0]);
        Assert.Equal(new string('a', 9) + "\n" + new string('b', 60), pieces[1]);
    }

    [Fact]
    public void Chunk_NewlineBeforeHalfWindow_IsIgnoredAsBreakPoint()
    {
        var pieces = DocumentChunker.Chunk(new string('a', 40) + "\n" + new string('b', 100), 100, 10);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(100, pieces[0].Length);
        Assert.Contains('\n', pieces[0]);
        Assert.Equal(new string('b', 51), pieces[1]);
    }

    [Fact]
    public void Chunk_WhitespaceOnlyPiece_IsSkipped()
    {
        var pieces = DocumentChunker.Chunk(
            new string('a', 80) + new string('\n', 100) + new string('b', 80), 100, 0);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(new string('a', 80), pieces[0]);
        Assert.Equal(new string('b', 80), pieces[1]);
    }

    [Fact]
    public void Chunk_ExactMultiple_BreaksWithoutTrailingDuplicate()
    {
        var pieces = DocumentChunker.Chunk(new string('a', 200), 100, 0);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(100, pieces[0].Length);
        Assert.Equal(100, pieces[1].Length);
    }

    [Fact]
    public void Chunk_NormalizesCrLfToLf()
    {
        var pieces = DocumentChunker.Chunk("line1\r\nline2\r\nline3", 500, 100);

        Assert.Single(pieces);
        Assert.Equal("line1\nline2\nline3", pieces[0]);
        Assert.DoesNotContain("\r", pieces[0]);
    }
}
