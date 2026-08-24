using System.Text.Json;

namespace DeskPet.Core.Chat;

/// <summary>
/// SSE <c>data:</c> 载荷与 OpenAI 兼容 JSON 响应解析器（Swift <c>parseStreamDelta</c> 500-510 /
/// <c>parseNonStreamContent</c> 513-524）。内部类型，经 InternalsVisibleTo 供单测聚焦覆盖。
/// </summary>
internal static class SsePayloadParser
{
    /// <summary>
    /// 解析一条 SSE 载荷的增量内容：<c>choices[0].delta.content</c>；
    /// 无该字段或内容为空返回 null（Swift 500-510）。
    /// </summary>
    public static string? ParseStreamDelta(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return ParseContent(doc, delta: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从完整 JSON body 提取 <c>choices[0].message.content</c>（非流式兜底）；
    /// 内容为空或结构不符返回 null（Swift 513-524，JSON 解析失败亦返回 null）。
    /// </summary>
    public static string? ParseNonStreamContent(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            return ParseContent(doc, delta: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>data[]</c> 向量列表：按 <c>index</c> 升序返回 <c>embedding</c>；
    /// 结构不符抛 InvalidResponse；返回数量与 <paramref name="expectedCount"/> 不符抛 InvalidResponse
    /// （Swift embed 331-342：按 index 排序 + compactMap + count 校验）。
    /// </summary>
    public static IReadOnlyList<float[]> ParseEmbeddings(string json, int expectedCount)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw PetChatException.InvalidResponse();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw PetChatException.InvalidResponse();
            }

            var entries = new List<(int Index, float[] Vector)>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw PetChatException.InvalidResponse();
                var index = item.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                    && idx.TryGetInt32(out var intIndex)
                    ? intIndex
                    : 0;
                if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
                {
                    throw PetChatException.InvalidResponse();
                }
                var vector = new float[embedding.GetArrayLength()];
                var k = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    if (!value.TryGetSingle(out var v)) throw PetChatException.InvalidResponse();
                    vector[k++] = v;
                }
                entries.Add((index, vector));
            }

            entries.Sort((a, b) => a.Index.CompareTo(b.Index));
            var result = entries.Select(e => e.Vector).ToList();
            if (result.Count != expectedCount) throw PetChatException.InvalidResponse();
            return result;
        }
    }

    /// <summary>提取 <c>choices[0]</c> 的内容：<paramref name="delta"/> 时取 <c>delta.content</c>，否则取 <c>message.content</c>。</summary>
    private static string? ParseContent(JsonDocument doc, bool delta)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }
        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object) return null;
        var container = delta ? "delta" : "message";
        if (!first.TryGetProperty(container, out var message) || message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
        var text = content.GetString() ?? string.Empty;
        return text.Length == 0 ? null : text;
    }
}
