namespace DeskPet.Core.RAG;

/// <summary>
/// 文本分块器，移植自 Swift <c>RAGManager.chunkText</c>（RAGManager.swift 244-277）：
/// 按字符窗口分块 + 相邻块 overlap 重叠；窗口后半段内优先在最后一个换行处断开；
/// 最小分块大小 100；\r\n 归一化为 \n；空白块跳过。
/// </summary>
public static class DocumentChunker
{
    /// <summary>分块大小下限（Swift <c>max(100, size)</c>）。</summary>
    public const int MinChunkSize = 100;

    /// <summary>
    /// 将文本切成带重叠的块（Swift chunkText 算法逐行移植）。
    /// </summary>
    /// <param name="text">源文本（null 抛 <see cref="ArgumentNullException"/>）。</param>
    /// <param name="chunkSize">目标分块大小（低于 100 时按 100 处理）。</param>
    /// <param name="overlap">相邻块重叠字符数（上限 maxSize-1）。</param>
    /// <returns>非空分块列表；空/纯空白文本返回空列表。</returns>
    public static IReadOnlyList<string> Chunk(string text, int chunkSize, int overlap)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n");
        var length = normalized.Length;
        if (length == 0) return Array.Empty<string>();

        var maxSize = Math.Max(MinChunkSize, chunkSize);
        var overlapAmount = Math.Min(overlap, maxSize - 1);
        var pieces = new List<string>();
        var start = 0;

        while (start < length)
        {
            var end = Math.Min(start + maxSize, length);
            // 尽量在段落末尾断开，避免把句子拦腰截断（仅搜索窗口后半段）。
            if (end < length)
            {
                var searchStart = start + Math.Max(0, maxSize / 2);
                var newline = normalized.LastIndexOf('\n', end - 1, end - searchStart);
                if (newline >= 0) end = newline + 1;
            }
            var piece = normalized.Substring(start, end - start).Trim();
            if (piece.Length > 0) pieces.Add(piece);
            if (end >= length) break;
            start = Math.Max(start + 1, end - overlapAmount);
        }
        return pieces;
    }
}
