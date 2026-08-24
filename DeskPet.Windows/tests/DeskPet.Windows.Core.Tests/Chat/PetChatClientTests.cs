using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Chat;
using DeskPet.Core.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Chat;

/// <summary>
/// Task 2 PetChatClient 行为测试（对照 Swift PetChatManager.swift 87-184 send / 266-283 testConnection /
/// 301-345 embed / 349-393 makeRequest / 460-476 endpointURL / 500-524 SSE 解析）。
/// FakeHttpMessageHandler 注入，覆盖：SSE 增量顺序与累积、[DONE] 终止、HTTP 400 → HttpError、
/// 空回复 → EmptyResponse、非流式 JSON 兜底、BaseUrl 补全 /v1、Bearer 头有无、
/// 消息组装（system prompt 在前、历史含 system 角色、suffix 裁剪、user 输入去重）、
/// Embedding 分批(32)/按 index 排序/数量校验。
/// </summary>
public class PetChatClientTests
{
    // MARK: - StreamChat

    [Fact]
    public async Task StreamChat_AccumulatesDeltasInOrderAndReportsProgress()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            SseResponse(
                """{"choices":[{"delta":{"role":"assistant"}}]}""",
                """{"choices":[{"delta":{"content":"你"}}]}""",
                """{"choices":[{"delta":{"content":"好"}}]}""",
                """{"choices":[{"delta":{"content":"呀"}}]}""",
                "[DONE]"));
        var progress = new ListProgress();
        var client = CreateClient(new FakeSettings(), handler);

        var reply = await client.StreamChatAsync(
            Array.Empty<ChatMessage>(), "hi", progress, CancellationToken.None);

        Assert.Equal("你好呀", reply);
        Assert.Equal(new[] { "你", "好", "呀" }, progress.Reported);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamChat_StopsProcessingAtDoneEvent()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            SseResponse(
                """{"choices":[{"delta":{"content":"你"}}]}""",
                "[DONE]",
                """{"choices":[{"delta":{"content":"后置内容不应被读取"}}]}"""));
        var client = CreateClient(new FakeSettings(), handler);

        var reply = await client.StreamChatAsync(
            Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None);

        Assert.Equal("你", reply);
    }

    [Fact]
    public async Task StreamChat_Http400_ThrowsHttpErrorWithTruncatedBody()
    {
        var longBody = new string('e', 400);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(longBody, Encoding.UTF8),
        });
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.HttpError, ex.Kind);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(300, ex.ErrorBody!.Length);
        Assert.StartsWith("请求失败（HTTP 400）：", ex.Message);
    }

    [Fact]
    public async Task StreamChat_OnlyDoneEvent_ThrowsEmptyResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.EmptyResponse, ex.Kind);
        Assert.Equal("模型没有返回内容", ex.Message);
    }

    [Fact]
    public async Task StreamChat_NoDataEvents_FallsBackToNonStreamJson()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { choices = new[] { new { message = new { content = "完整回复" } } } }));
        var progress = new ListProgress();
        var client = CreateClient(new FakeSettings(), handler);

        var reply = await client.StreamChatAsync(
            Array.Empty<ChatMessage>(), "hi", progress, CancellationToken.None);

        Assert.Equal("完整回复", reply);
        Assert.Empty(progress.Reported);
    }

    [Fact]
    public async Task StreamChat_NonStreamJsonWithEmptyContent_ThrowsEmptyResponse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { choices = new[] { new { message = new { content = "" } } } }));
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.EmptyResponse, ex.Kind);
    }

    [Fact]
    public async Task StreamChat_SystemPromptFirst_ThenHistoryIncludingSystemRoles()
    {
        var settings = new FakeSettings { ChatSystemPrompt = "SET_PROMPT" };
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(settings, handler);
        var history = new[]
        {
            Msg(ChatRole.System, "SYS1"),
            Msg(ChatRole.User, "u1"),
            Msg(ChatRole.Assistant, "a1"),
            Msg(ChatRole.System, "SYS2"),
            Msg(ChatRole.User, "u2"),
        };

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(history, "hello", null, CancellationToken.None));

        var req = Assert.Single(handler.Requests);
        var messages = ReadMessages(req);
        Assert.Equal(
            new[]
            {
                ("system", "SET_PROMPT"),
                ("system", "SYS1"),
                ("user", "u1"),
                ("assistant", "a1"),
                ("system", "SYS2"),
                ("user", "u2"),
                ("user", "hello"),
            },
            messages);
    }

    [Fact]
    public async Task StreamChat_CapsHistorySuffixAtChatHistoryCount()
    {
        var settings = new FakeSettings { ChatHistoryCount = 2 };
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(settings, handler);
        var history = new[]
        {
            Msg(ChatRole.User, "m1"),
            Msg(ChatRole.User, "m2"),
            Msg(ChatRole.User, "m3"),
            Msg(ChatRole.User, "m4"),
            Msg(ChatRole.User, "m5"),
        };

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(history, "z", null, CancellationToken.None));

        var req = Assert.Single(handler.Requests);
        var messages = ReadMessages(req);
        Assert.Equal(
            new[]
            {
                ("system", "SET_PROMPT"),
                ("user", "m4"),
                ("user", "m5"),
                ("user", "z"),
            },
            messages);
    }

    [Fact]
    public async Task StreamChat_SkipsDuplicateUserInputWhenAlreadyLast()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings(), handler);
        var history = new[] { Msg(ChatRole.User, "a"), Msg(ChatRole.User, "b"), Msg(ChatRole.User, "c") };

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(history, "c", null, CancellationToken.None));

        var req = Assert.Single(handler.Requests);
        var messages = ReadMessages(req);
        Assert.Equal(
            new[]
            {
                ("system", "SET_PROMPT"),
                ("user", "a"),
                ("user", "b"),
                ("user", "c"),
            },
            messages);
    }

    [Fact]
    public async Task StreamChat_PostsToChatCompletionsWithModelStreamAndTemperature()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatModel = "test-model" }, handler);

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        var req = Assert.Single(handler.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("https://example.com/v1/chat/completions", req.Uri);
        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task StreamChat_AppendsV1ToBareDomain()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatBaseUrl = "https://example.com/" }, handler);

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal("https://example.com/v1/chat/completions", Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async Task StreamChat_ReusesBaseWhenItAlreadyEndsWithChatCompletionsPath()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(
            new FakeSettings { ChatBaseUrl = "https://example.com/v1/chat/completions" }, handler);

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal("https://example.com/v1/chat/completions", Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async Task StreamChat_SendsBearerTokenWhenKeySet()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatApiKey = "sk-123" }, handler);

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal("Bearer sk-123", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task StreamChat_OmitsAuthorizationHeaderWhenKeyEmpty()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatApiKey = "" }, handler);

        await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task StreamChat_NotEnabled_ThrowsNotEnabledWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatEnabled = false }, handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.NotEnabled, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamChat_EmptyBaseUrl_ThrowsNotConfiguredWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatBaseUrl = "" }, handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.NotConfigured, ex.Kind);
        Assert.Contains("Base URL", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamChat_MalformedBaseUrl_ThrowsInvalidUrl()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings { ChatBaseUrl = "ht!tp://exa mple.com" }, handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.InvalidUrl, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamChat_EmptyUserInput_ReturnsEmptyWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings(), handler);

        var reply = await client.StreamChatAsync(
            Array.Empty<ChatMessage>(), "   ", null, CancellationToken.None);

        Assert.Equal("", reply);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamChat_CancelledToken_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpMessageHandler(_ => SseResponse("[DONE]"));
        var client = CreateClient(new FakeSettings(), handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.StreamChatAsync(Array.Empty<ChatMessage>(), "hi", null, cts.Token));
    }

    // MARK: - TestConnection

    [Fact]
    public async Task TestConnection_Success_ReturnsTrueAndSendsNonStreamRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { choices = new[] { new { message = new { content = "OK" } } } }));
        var client = CreateClient(new FakeSettings(), handler);

        var ok = await client.TestConnectionAsync(CancellationToken.None);

        Assert.True(ok);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://example.com/v1/chat/completions", req.Uri);
        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("请只回复两个字符：OK", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestConnection_Http400_ThrowsHttpError()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"bad key\"}", Encoding.UTF8),
        });
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(PetChatErrorKind.HttpError, ex.Kind);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task TestConnection_EmptyContent_ThrowsEmptyResponse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(new { choices = new[] { new { message = new { content = "" } } } }));
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.TestConnectionAsync(CancellationToken.None));

        Assert.Equal(PetChatErrorKind.EmptyResponse, ex.Kind);
    }

    // MARK: - Embed

    [Fact]
    public async Task Embed_AllTextsEmpty_ReturnsEmptyWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(new { data = Array.Empty<object>() }));
        var client = CreateClient(new FakeSettings(), handler);

        var result = await client.EmbedAsync(new[] { "", "  " }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Embed_FiltersEmptyStringsAndPostsBatch()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            using var doc = JsonDocument.Parse(req.Body);
            var input = doc.RootElement.GetProperty("input");
            Assert.Equal(2, input.GetArrayLength());
            return JsonResponse(new
            {
                data = new[]
                {
                    new { index = 0, embedding = new[] { 1.0f, 0.0f } },
                    new { index = 1, embedding = new[] { 0.0f, 1.0f } },
                },
            });
        });
        var client = CreateClient(new FakeSettings(), handler);

        var result = await client.EmbedAsync(new[] { "a", "", "b" }, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { 1.0f, 0.0f }, result[0]);
        Assert.Equal(new[] { 0.0f, 1.0f }, result[1]);
        Assert.Equal("https://example.com/v1/embeddings", Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async Task Embed_EmptyModel_ThrowsNotConfigured()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(new { data = Array.Empty<object>() }));
        var client = CreateClient(new FakeSettings { EmbeddingModel = "" }, handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.EmbedAsync(new[] { "a" }, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.NotConfigured, ex.Kind);
        Assert.Contains("Embedding", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Embed_SortsVectorsByServerIndex()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(new
        {
            data = new[]
            {
                new { index = 1, embedding = new[] { 2.0f, 2.0f } },
                new { index = 0, embedding = new[] { 1.0f, 1.0f } },
            },
        }));
        var client = CreateClient(new FakeSettings(), handler);

        var result = await client.EmbedAsync(new[] { "first", "second" }, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { 1.0f, 1.0f }, result[0]);
        Assert.Equal(new[] { 2.0f, 2.0f }, result[1]);
    }

    [Fact]
    public async Task Embed_BatchesBy32WithModelAndBearer()
    {
        var settings = new FakeSettings { ChatApiKey = "sk-embed" };
        var handler = new FakeHttpMessageHandler(req =>
        {
            using var doc = JsonDocument.Parse(req.Body);
            var input = doc.RootElement.GetProperty("input");
            Assert.Equal("test-embed-model", doc.RootElement.GetProperty("model").GetString());
            var n = input.GetArrayLength();
            var data = new List<object>();
            for (var i = 0; i < n; i++) data.Add(new { index = i, embedding = new[] { (float)i, 0.0f } });
            return JsonResponse(new { data });
        });
        var client = CreateClient(settings, handler);

        var texts = Enumerable.Range(0, 70).Select(i => $"t{i}").ToArray();
        var result = await client.EmbedAsync(texts, CancellationToken.None);

        Assert.Equal(70, result.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, req => Assert.Equal("Bearer sk-embed", req.Authorization));
        Assert.All(handler.Requests, req => Assert.Equal("https://example.com/v1/embeddings", req.Uri));
        using var d0 = JsonDocument.Parse(handler.Requests[0].Body);
        using var d1 = JsonDocument.Parse(handler.Requests[1].Body);
        using var d2 = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal(32, d0.RootElement.GetProperty("input").GetArrayLength());
        Assert.Equal(32, d1.RootElement.GetProperty("input").GetArrayLength());
        Assert.Equal(6, d2.RootElement.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task Embed_FewerVectorsThanInputs_ThrowsInvalidResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(new
        {
            data = new[] { new { index = 0, embedding = new[] { 1.0f } } },
        }));
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.EmbedAsync(new[] { "a", "b" }, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Embed_MissingDataField_ThrowsInvalidResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(new { foo = "bar" }));
        var client = CreateClient(new FakeSettings(), handler);

        var ex = await Assert.ThrowsAsync<PetChatException>(() =>
            client.EmbedAsync(new[] { "a" }, CancellationToken.None));

        Assert.Equal(PetChatErrorKind.InvalidResponse, ex.Kind);
    }

    // MARK: - SsePayloadParser（内部解析器聚焦测试）

    [Fact]
    public void SsePayloadParser_ParsesStreamDeltaContent()
    {
        Assert.Equal("你", SsePayloadParser.ParseStreamDelta("""{"choices":[{"delta":{"content":"你"}}]}"""));
    }

    [Fact]
    public void SsePayloadParser_StreamDeltaWithoutContent_ReturnsNull()
    {
        Assert.Null(SsePayloadParser.ParseStreamDelta("""{"choices":[{"delta":{"role":"assistant"}}]}"""));
        Assert.Null(SsePayloadParser.ParseStreamDelta("""{"choices":[]}"""));
        Assert.Null(SsePayloadParser.ParseStreamDelta("""{"choices":[{"message":{"content":"x"}}]}"""));
    }

    [Fact]
    public void SsePayloadParser_MalformedJson_ReturnsNull()
    {
        Assert.Null(SsePayloadParser.ParseStreamDelta("not json"));
        Assert.Null(SsePayloadParser.ParseStreamDelta(""));
    }

    [Fact]
    public void SsePayloadParser_ParsesNonStreamContent()
    {
        Assert.Equal("完整回复", SsePayloadParser.ParseNonStreamContent(
            """{"choices":[{"message":{"content":"完整回复"}}]}"""));
        Assert.Null(SsePayloadParser.ParseNonStreamContent("""{"choices":[{"message":{"content":""}}]}"""));
        Assert.Null(SsePayloadParser.ParseNonStreamContent("garbage"));
    }

    // MARK: - 测试辅助

    private static ChatMessage Msg(ChatRole role, string content) =>
        new(Guid.NewGuid(), role, content, IsStreaming: false);

    private static PetChatClient CreateClient(FakeSettings settings, FakeHttpMessageHandler handler) =>
        new(settings, new HttpClient(handler));

    private static HttpResponseMessage SseResponse(params string[] payloads)
    {
        var sb = new StringBuilder();
        foreach (var payload in payloads) sb.Append("data: ").Append(payload).Append("\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8),
        };
    }

    private static HttpResponseMessage JsonResponse(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static List<(string Role, string Content)> ReadMessages(CapturedRequest request)
    {
        using var doc = JsonDocument.Parse(request.Body);
        var messages = new List<(string, string)>();
        foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            messages.Add((m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!));
        }
        return messages;
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Reported { get; } = new();

        public void Report(string value) => Reported.Add(value);
    }

    private sealed record CapturedRequest(string Method, string Uri, string? Authorization, string Body);

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> responder) =>
            _responder = responder;

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(
                request.Method.ToString(),
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                body);
            Requests.Add(captured);
            return _responder(captured);
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public double PetScale { get; set; } = 1.0;
        public double RoamInterval { get; set; } = 4.0;
        public double StatDecaySpeed { get; set; } = 1.0;
        public double SoundVolume { get; set; } = 0.8;
        public bool MusicDance { get; set; }
        public string SkinDirectory { get; set; } = "";
        public bool ChatEnabled { get; set; } = true;
        public string ChatBaseUrl { get; set; } = "https://example.com";
        public string ChatApiKey { get; set; } = "";
        public string ChatModel { get; set; } = "test-model";
        public string ChatSystemPrompt { get; set; } = "SET_PROMPT";
        public int ChatHistoryCount { get; set; } = 20;
        public double BubbleIntervalSeconds { get; set; } = 120;
        public int BubbleMaxLength { get; set; } = 120;
        public string EmbeddingModel { get; set; } = "test-embed-model";
        public bool RagEnabled { get; set; }
    }
}
