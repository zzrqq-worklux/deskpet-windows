using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeskPet.Core.Services;

namespace DeskPet.Core.Chat;

/// <summary>
/// OpenAI 兼容对话 + Embedding 客户端（Swift <c>PetChatManager.swift</c> 移植，T2 范围）：
/// <c>StreamChatAsync</c> ↔ <c>send</c>（87-184，SSE 流式 + 非流式 JSON 兜底），
/// <c>TestConnectionAsync</c> ↔ <c>testConnection</c>（266-283），
/// <c>EmbedAsync</c> ↔ <c>embed</c>（301-345，批量 32、按 index 排序），
/// 请求构建 ↔ <c>makeRequest</c>（349-393），Base URL 补全 ↔ <c>endpointURL</c>（460-476），
/// SSE/JSON 解析 ↔ <c>parseStreamDelta</c>/<c>parseNonStreamContent</c>（500-524）。
/// </summary>
public sealed class PetChatClient : IChatClient, IEmbeddingService
{
    /// <summary>Embedding 单批条数（Swift <c>nonEmpty.batched(by: 32)</c>）。</summary>
    private const int EmbeddingBatchSize = 32;

    /// <summary>错误响应体截断长度（Swift <c>collectBody</c> 300）。</summary>
    private const int ErrorBodyLimit = 300;

    /// <summary>聊天补全路径（Swift <c>endpointURL(path:)</c>）。</summary>
    private const string ChatCompletionsPath = "chat/completions";

    /// <summary>Embedding 路径（Swift <c>endpointURL(path:)</c>）。</summary>
    private const string EmbeddingsPath = "embeddings";

    /// <summary>采样温度（Swift <c>makeRequest</c> 0.7）。</summary>
    private const double Temperature = 0.7;

    private readonly ISettingsService _settings;
    private readonly HttpClient _http;

    /// <param name="settings">设置服务（对话/模型/Key 均来自此处，Swift <c>Defaults[...]</c>）。</param>
    /// <param name="http">注入的 HttpClient（测试注入 FakeHttpMessageHandler 的入口）。</param>
    public PetChatClient(ISettingsService settings, HttpClient http)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// 流式发送用户输入，返回完整回复（Swift <c>PetChatManager.send</c> 的 SSE 流式累积）。
    /// 请求体 = [设置 system prompt?] + 历史（含 System 角色，suffix 裁剪到 max(1, ChatHistoryCount)）
    /// + 本次用户输入（若与历史末条内容相同则不重复追加）；
    /// 增量按 <c>choices[0].delta.content</c> 逐个经 <paramref name="progress"/> 上报，
    /// <c>[DONE]</c> 终止；无任何 data 事件时尝试非流式 JSON 兜底（Swift 168-170）。
    /// </summary>
    /// <param name="history">历史消息（Task 7 经 System 消息注入 RAG 上下文，随历史一并发送）。</param>
    /// <param name="userInput">本次用户输入（空白输入返回空串、不发请求，对应 Swift send 的空输入早退）。</param>
    /// <param name="progress">流式增量回调（仅上报非空内容分片），可为 null。</param>
    /// <param name="ct">取消令牌（贯穿发送与 SSE 读取）。</param>
    /// <exception cref="PetChatException">
    /// <see cref="PetChatErrorKind.NotEnabled"/> 未启用对话 /
    /// <see cref="PetChatErrorKind.NotConfigured"/> Base URL 为空 /
    /// <see cref="PetChatErrorKind.InvalidUrl"/> Base URL 非法 /
    /// <see cref="PetChatErrorKind.HttpError"/> HTTP 非 200 /
    /// <see cref="PetChatErrorKind.EmptyResponse"/> 无任何回复内容。
    /// </exception>
    public async Task<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        string userInput,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!_settings.ChatEnabled) throw PetChatException.NotEnabled();

        var userText = userInput.Trim();
        if (userText.Length == 0) return string.Empty;

        var endpoint = BuildEndpoint(_settings.ChatBaseUrl, ChatCompletionsPath);
        var messages = BuildMessages(history, userText);
        var body = JsonSerializer.Serialize(new
        {
            model = _settings.ChatModel,
            messages,
            stream = true,
            temperature = Temperature,
        });

        using var request = CreateRequest(endpoint, body);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode != 200)
        {
            var errorBody = await CollectBodyAsync(response, ct);
            throw PetChatException.HttpError((int)response.StatusCode, errorBody);
        }

        var content = await ReadSseAsync(response, progress, ct);
        if (content.Length == 0) throw PetChatException.EmptyResponse();
        return content;
    }

    /// <summary>
    /// 测试与 API 的连接（Swift <c>testConnection</c>）：非流式请求，
    /// 成功返回 true；失败抛 <see cref="PetChatException"/>（HttpError/EmptyResponse 等，与 Swift 一致）。
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        var endpoint = BuildEndpoint(_settings.ChatBaseUrl, ChatCompletionsPath);
        var messages = new List<ApiMessage>();
        if (!string.IsNullOrEmpty(_settings.ChatSystemPrompt))
        {
            messages.Add(new ApiMessage("system", _settings.ChatSystemPrompt));
        }
        messages.Add(new ApiMessage("user", "请只回复两个字符：OK"));
        var body = JsonSerializer.Serialize(new
        {
            model = _settings.ChatModel,
            messages,
            stream = false,
            temperature = Temperature,
        });

        using var request = CreateRequest(endpoint, body);
        using var response = await _http.SendAsync(request, ct);
        if ((int)response.StatusCode != 200)
        {
            var errorBody = await CollectBodyAsync(response, ct);
            throw PetChatException.HttpError((int)response.StatusCode, errorBody);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (SsePayloadParser.ParseNonStreamContent(json) is null) throw PetChatException.EmptyResponse();
        return true;
    }

    /// <summary>
    /// 批量生成文本向量（Swift <c>embed</c>）：过滤空白文本、每批 32 条、
    /// 响应按 <c>index</c> 排序后按输入顺序返回；批次数量不符抛 InvalidResponse。
    /// </summary>
    /// <exception cref="PetChatException">
    /// <see cref="PetChatErrorKind.NotConfigured"/> Embedding 模型名为空 /
    /// <see cref="PetChatErrorKind.InvalidUrl"/> Base URL 非法 /
    /// <see cref="PetChatErrorKind.HttpError"/> HTTP 非 200 /
    /// <see cref="PetChatErrorKind.InvalidResponse"/> 响应缺少 data/embedding 或数量不符。
    /// </exception>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var nonEmpty = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (nonEmpty.Count == 0) return Array.Empty<float[]>();

        var model = _settings.EmbeddingModel.Trim();
        if (model.Length == 0) throw PetChatException.NotConfigured("请先在 设置 → RAG 中填写 Embedding 模型名");

        var endpoint = BuildEndpoint(_settings.ChatBaseUrl, EmbeddingsPath);
        var results = new List<float[]>();
        for (var offset = 0; offset < nonEmpty.Count; offset += EmbeddingBatchSize)
        {
            var batch = nonEmpty.GetRange(offset, Math.Min(EmbeddingBatchSize, nonEmpty.Count - offset));
            var body = JsonSerializer.Serialize(new { model, input = batch });

            using var request = CreateRequest(endpoint, body);
            using var response = await _http.SendAsync(request, ct);
            if ((int)response.StatusCode != 200)
            {
                var errorBody = await CollectBodyAsync(response, ct);
                throw PetChatException.HttpError((int)response.StatusCode, errorBody);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            results.AddRange(SsePayloadParser.ParseEmbeddings(json, batch.Count));
        }
        return results;
    }

    // MARK: - 请求构建（Swift makeRequest 349-393 / endpointURL 460-476）

    /// <summary>
    /// 合成请求体 messages（控制器裁定 R3：设置 system prompt 在前，历史按序全量发送——含 System 角色，
    /// 便于 Task 7 经历史注入 RAG 上下文；再按 Swift suffix 语义裁剪并追加本次用户输入）。
    /// </summary>
    private List<ApiMessage> BuildMessages(IReadOnlyList<ChatMessage> history, string userText)
    {
        var messages = new List<ApiMessage>();
        if (!string.IsNullOrEmpty(_settings.ChatSystemPrompt))
        {
            messages.Add(new ApiMessage("system", _settings.ChatSystemPrompt));
        }

        var maxHistory = Math.Max(1, _settings.ChatHistoryCount);
        var historyToSend = history.Skip(Math.Max(0, history.Count - maxHistory)).ToList();
        if (historyToSend.Count == 0 || historyToSend[^1].Content != userText)
        {
            historyToSend.Add(new ChatMessage(Guid.NewGuid(), ChatRole.User, userText, IsStreaming: false));
        }

        foreach (var msg in historyToSend)
        {
            messages.Add(new ApiMessage(RoleName(msg.Role), msg.Content));
        }
        return messages;
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.System => "system",
        _ => "user",
    };

    private HttpRequestMessage CreateRequest(Uri endpoint, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var apiKey = _settings.ChatApiKey.Trim();
        if (apiKey.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// 合成完整 API URL（Swift <c>endpointURL</c> 460-476）：去首尾空白与尾部斜杠；
    /// 已以 <paramref name="path"/> 结尾则原样使用，否则补 <c>/v1</c> 后再接 <c>/{path}</c>。
    /// Base URL 为空抛 NotConfigured（控制器裁定 R2）；无法解析为绝对地址抛 InvalidUrl
    /// （Swift 的 relative URL 在 HttpClient 下不可用，此为一处有意收紧）。
    /// </summary>
    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw PetChatException.NotConfigured("请先在 设置 → Chat 中填写 Base URL");
        }

        var trimmed = baseUrl.Trim();
        while (trimmed.EndsWith('/')) trimmed = trimmed[..^1];

        if (trimmed.EndsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var direct)) throw PetChatException.InvalidUrl();
            return direct;
        }

        var urlString = trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/v1";
        urlString += "/" + path;
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) throw PetChatException.InvalidUrl();
        return uri;
    }

    // MARK: - SSE 读取（Swift send 145-170）

    private static async Task<string> ReadSseAsync(
        HttpResponseMessage response, IProgress<string>? progress, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var rawBody = new StringBuilder();
        var sawDataEvent = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            rawBody.Append(line).Append('\n');
            var trimmedLine = line.Trim();
            if (!trimmedLine.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = trimmedLine["data:".Length..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]")
            {
                sawDataEvent = true;
                break;
            }
            sawDataEvent = true;
            var piece = SsePayloadParser.ParseStreamDelta(payload);
            if (piece is not null)
            {
                content.Append(piece);
                progress?.Report(piece);
            }
        }

        if (!sawDataEvent)
        {
            var full = SsePayloadParser.ParseNonStreamContent(rawBody.ToString());
            if (full is not null) return full;
        }
        return content.ToString();
    }

    /// <summary>读取错误响应体并截断（Swift <c>collectBody</c> 527-534，行间空格拼接、上限 300）。</summary>
    private static async Task<string> CollectBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var body = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            body.Append(line).Append(' ');
            if (body.Length > ErrorBodyLimit) break;
        }
        return body.Length > ErrorBodyLimit ? body.ToString(0, ErrorBodyLimit) : body.ToString();
    }

    /// <summary>请求体单条消息（序列化为 <c>{"role": ..., "content": ...}</c>）。</summary>
    private sealed record ApiMessage(string role, string content);
}
