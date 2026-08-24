using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Chat;
using DeskPet.Core.RAG;
using DeskPet.Core.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Chat;

/// <summary>
/// Task 7 RagChatPipeline 测试（对照 Swift PetChatManager.send 124-134 RAG 块 +
/// VectorStoreManager.contextPrompt 82-95）：
/// RagEnabled 注入（system 消息置于历史首位 + 用户消息仍在）、禁用透传、
/// 空结果/embedding 空返回/检索抛异常静默跳过（Swift try?）、topK=3、
/// 2000 字符预算逐条丢弃、长历史时预留容量保证注入消息不被内层 suffix 裁剪。
/// </summary>
public class RagChatPipelineTests : IDisposable
{
    private const string Prefix = "以下是宠物主人提供的参考资料（可能与问题相关，也可能无关，请按需采用，不要编造）：\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetTests", Guid.NewGuid().ToString("N"));

    // MARK: - 注入语义

    [Fact]
    public async Task RagEnabled_StoreHasResults_PrependsSystemContextAndKeepsUserMessage()
    {
        var settings = new RagSettings { RagEnabled = true };
        var store = new LocalVectorStore(_dir);
        await store.UpsertAsync(new[]
        {
            Doc("a", "苹果很好吃", new[] { 1f, 0f, 0f }),
            Doc("b", "桌宠爱睡觉", new[] { 0f, 1f, 0f }),
        }, CancellationToken.None);
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(
            inner, new FixedVectorEmbedding(new[] { 1f, 0f, 0f }), store, settings);
        var user = new ChatMessage(Guid.NewGuid(), ChatRole.User, "苹果好吃吗？", IsStreaming: false);

        var reply = await pipeline.StreamChatAsync(new[] { user }, user.Content, null, CancellationToken.None);

        Assert.Equal("pong", reply);
        var captured = inner.LastHistory!;
        Assert.Equal(ChatRole.System, captured[0].Role);
        Assert.StartsWith(Prefix, captured[0].Content);
        Assert.Contains("• 苹果很好吃", captured[0].Content);
        Assert.Contains("• 桌宠爱睡觉", captured[0].Content);
        Assert.True(captured[0].Content.IndexOf("• 苹果很好吃", StringComparison.Ordinal)
            < captured[0].Content.IndexOf("• 桌宠爱睡觉", StringComparison.Ordinal));
        Assert.Equal(user, captured[^1]);
    }

    [Fact]
    public async Task RagDisabled_HistoryPassesThroughWithoutInjection()
    {
        var settings = new RagSettings { RagEnabled = false };
        var store = new LocalVectorStore(_dir);
        await store.UpsertAsync(new[] { Doc("a", "苹果很好吃", new[] { 1f, 0f, 0f }) }, CancellationToken.None);
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(inner, new FixedVectorEmbedding(new[] { 1f, 0f, 0f }), store, settings);
        var history = new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) };

        await pipeline.StreamChatAsync(history, "hi", null, CancellationToken.None);

        Assert.Equal(history[0], Assert.Single(inner.LastHistory!));
        Assert.DoesNotContain(inner.LastHistory!, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task RagEnabled_EmptyStore_NoInjectionAndChatCompletes()
    {
        var settings = new RagSettings { RagEnabled = true };
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(
            inner, new FixedVectorEmbedding(new[] { 1f, 0f, 0f }), new LocalVectorStore(_dir), settings);
        var history = new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) };

        var reply = await pipeline.StreamChatAsync(history, "hi", null, CancellationToken.None);

        Assert.Equal("pong", reply);
        Assert.Equal(history[0], Assert.Single(inner.LastHistory!));
        Assert.DoesNotContain(inner.LastHistory!, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task RagEnabled_EmbeddingReturnsEmpty_NoInjectionAndChatCompletes()
    {
        var settings = new RagSettings { RagEnabled = true };
        var store = new LocalVectorStore(_dir);
        await store.UpsertAsync(new[] { Doc("a", "苹果很好吃", new[] { 1f, 0f, 0f }) }, CancellationToken.None);
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(inner, new EmptyEmbedding(), store, settings);
        var history = new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) };

        var reply = await pipeline.StreamChatAsync(history, "hi", null, CancellationToken.None);

        Assert.Equal("pong", reply);
        Assert.Equal(history[0], Assert.Single(inner.LastHistory!));
    }

    [Fact]
    public async Task RagEnabled_EmbeddingThrows_PassesThroughAndChatCompletes()
    {
        var settings = new RagSettings { RagEnabled = true };
        var store = new LocalVectorStore(_dir);
        await store.UpsertAsync(new[] { Doc("a", "苹果很好吃", new[] { 1f, 0f, 0f }) }, CancellationToken.None);
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(
            inner,
            new FixedVectorEmbedding(new[] { 1f, 0f, 0f }, throwOnCall: new InvalidOperationException("embedding down")),
            store, settings);
        var history = new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) };

        var reply = await pipeline.StreamChatAsync(history, "hi", null, CancellationToken.None);

        Assert.Equal("pong", reply);
        Assert.Equal(1, inner.InvocationCount);
        Assert.Equal(history[0], Assert.Single(inner.LastHistory!));
        Assert.DoesNotContain(inner.LastHistory!, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task RagEnabled_SearchThrows_PassesThroughAndChatCompletes()
    {
        var settings = new RagSettings { RagEnabled = true };
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(
            inner,
            new FixedVectorEmbedding(new[] { 1f, 0f, 0f }),
            new ThrowingStore(),
            settings);
        var history = new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) };

        var reply = await pipeline.StreamChatAsync(history, "hi", null, CancellationToken.None);

        Assert.Equal("pong", reply);
        Assert.Equal(1, inner.InvocationCount);
        Assert.Equal(history[0], Assert.Single(inner.LastHistory!));
    }

    // MARK: - topK / 历史容量

    [Fact]
    public async Task RagEnabled_TopK_OnlyThreeResultsInjected()
    {
        var settings = new RagSettings { RagEnabled = true };
        var store = new LocalVectorStore(_dir);
        await store.UpsertAsync(Enumerable.Range(0, 5)
            .Select(i => Doc($"d{i}", $"chunk {i}", new[] { 1f })).ToList(), CancellationToken.None);
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(inner, new FixedVectorEmbedding(new[] { 1f }), store, settings);
        var history = new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) };

        await pipeline.StreamChatAsync(history, "hi", null, CancellationToken.None);

        var lines = inner.LastHistory![0].Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Count(l => l.StartsWith("• ")));
        Assert.DoesNotContain(inner.LastHistory[0].Content, "chunk 3");
        Assert.DoesNotContain(inner.LastHistory[0].Content, "chunk 4");
    }

    [Fact]
    public async Task RagEnabled_LongHistory_ReservesRoomSoSystemMessageSurvivesInnerTrim()
    {
        var settings = new RagSettings { RagEnabled = true, ChatHistoryCount = 3 };
        var store = new LocalVectorStore(_dir);
        await store.UpsertAsync(new[] { Doc("a", "苹果很好吃", new[] { 1f, 0f, 0f }) }, CancellationToken.None);
        var inner = new CapturingChat();
        var pipeline = new RagChatPipeline(inner, new FixedVectorEmbedding(new[] { 1f, 0f, 0f }), store, settings);
        var history = new[]
        {
            new ChatMessage(Guid.NewGuid(), ChatRole.User, "u1", IsStreaming: false),
            new ChatMessage(Guid.NewGuid(), ChatRole.Assistant, "a1", IsStreaming: false),
            new ChatMessage(Guid.NewGuid(), ChatRole.User, "u2", IsStreaming: false),
            new ChatMessage(Guid.NewGuid(), ChatRole.Assistant, "a2", IsStreaming: false),
            new ChatMessage(Guid.NewGuid(), ChatRole.User, "u3", IsStreaming: false),
        };

        await pipeline.StreamChatAsync(history, "u3", null, CancellationToken.None);

        var captured = inner.LastHistory!;
        Assert.Equal(3, captured.Count);
        Assert.Equal(ChatRole.System, captured[0].Role);
        Assert.Equal("a2", captured[1].Content);
        Assert.Equal("u3", captured[2].Content);
    }

    // MARK: - 上下文格式（Swift VectorStoreManager.contextPrompt 82-95）

    [Fact]
    public void BuildContextPrompt_EmptyResults_ReturnsNull()
    {
        Assert.Null(RagChatPipeline.BuildContextPrompt(Array.Empty<VectorSearchResult>()));
    }

    [Fact]
    public void BuildContextPrompt_MatchesSwiftPrefixAndBulletFormat()
    {
        var prompt = RagChatPipeline.BuildContextPrompt(new[]
        {
            new VectorSearchResult("a", "苹果很好吃", 0.9f),
            new VectorSearchResult("b", "桌宠爱睡觉", 0.8f),
        });

        Assert.Equal(Prefix + "• 苹果很好吃\n• 桌宠爱睡觉", prompt);
    }

    [Fact]
    public void BuildContextPrompt_ReplacesNewlinesWithSpaces()
    {
        var prompt = RagChatPipeline.BuildContextPrompt(new[]
        {
            new VectorSearchResult("a", "第一行\n第二行", 1f),
        });

        Assert.Equal(Prefix + "• 第一行 第二行", prompt);
    }

    [Fact]
    public void BuildContextPrompt_FirstLineOverBudget_ReturnsNull()
    {
        var prompt = RagChatPipeline.BuildContextPrompt(new[]
        {
            new VectorSearchResult("a", new string('x', 2000), 1f),
        });

        Assert.Null(prompt);
    }

    [Fact]
    public void BuildContextPrompt_DropsOverBudgetLineAndEverythingAfter()
    {
        var text1 = new string('a', 1000);   // "• " + 1000 → 剩余 2000-1002-1 = 997
        var over = new string('b', 996);     // "• " + 996 → 998 > 997 → break，其后全部丢弃（Swift break 语义）

        var prompt = RagChatPipeline.BuildContextPrompt(new[]
        {
            new VectorSearchResult("a", text1, 0.9f),
            new VectorSearchResult("b", over, 0.8f),
            new VectorSearchResult("c", "short", 0.7f),
        });

        Assert.Equal(Prefix + "• " + text1, prompt);
        Assert.DoesNotContain("b", prompt);
        Assert.DoesNotContain("short", prompt);
    }

    [Fact]
    public void BuildContextPrompt_LineAtExactRemainingBudget_IsIncluded()
    {
        var text1 = new string('a', 1000);   // 剩余 997
        var atBudget = new string('c', 995); // "• " + 995 → 997 == 剩余 → 保留（Swift 仅 > 才 break）

        var prompt = RagChatPipeline.BuildContextPrompt(new[]
        {
            new VectorSearchResult("a", text1, 0.9f),
            new VectorSearchResult("c", atBudget, 0.7f),
        });

        Assert.Equal(Prefix + "• " + text1 + "\n• " + atBudget, prompt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // MARK: - 测试辅助

    private static VectorDocument Doc(string id, string text, float[] embedding)
        => new(id, text, embedding, null);

    /// <summary>捕获发给它的历史与输入；固定返回 "pong"（Swift 风格回执）。</summary>
    private sealed class CapturingChat : IChatClient
    {
        public List<ChatMessage>? LastHistory { get; private set; }
        public int InvocationCount { get; private set; }

        public Task<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> history, string userInput,
            IProgress<string>? progress, CancellationToken ct)
        {
            InvocationCount++;
            LastHistory = history.ToList();
            return Task.FromResult("pong");
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>固定向量的 embedding 服务；可配置首次调用抛异常。</summary>
    private sealed class FixedVectorEmbedding : IEmbeddingService
    {
        private readonly float[] _vector;
        private readonly Exception? _throwOnCall;

        public FixedVectorEmbedding(float[] vector, Exception? throwOnCall = null)
        {
            _vector = vector;
            _throwOnCall = throwOnCall;
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            if (_throwOnCall is not null) throw _throwOnCall;
            var result = texts.Select(_ => _vector).ToList();
            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }
    }

    /// <summary>始终返回空向量列表（模拟 embedding 服务无输出，对应 Swift embed 返回空）。</summary>
    private sealed class EmptyEmbedding : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
    }

    /// <summary>任何检索调用都抛异常（模拟向量库故障）。</summary>
    private sealed class ThrowingStore : IVectorStore
    {
        public Task UpsertAsync(IReadOnlyList<VectorDocument> docs, CancellationToken ct)
            => throw new InvalidOperationException("store down");

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            string query, float[]? queryEmbedding, int topK, CancellationToken ct)
            => throw new InvalidOperationException("store down");

        public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => throw new InvalidOperationException("store down");

        public Task ClearAsync(CancellationToken ct)
            => throw new InvalidOperationException("store down");
    }

    private sealed class RagSettings : ISettingsService
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
