using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Chat;
using DeskPet.Core.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Chat;

/// <summary>
/// Task 1 对话契约冒烟测试（Swift PetChatManager.swift 对照）：
/// 类型存在、ChatMessage JSON 往返、ISettingsService 新增聊天/RAG 属性默认值。
/// 行为测试由后续任务（T2/T5）负责。
/// </summary>
public class ContractsTests
{
    [Fact]
    public void ChatMessage_JsonRoundTrip_PreservesAllFields()
    {
        var id = Guid.NewGuid();
        var msg = new ChatMessage(id, ChatRole.Assistant, "你好，主人~", IsStreaming: true);

        var json = JsonSerializer.Serialize(msg);
        var back = JsonSerializer.Deserialize<ChatMessage>(json);

        Assert.NotNull(back);
        Assert.Equal(msg, back);
        Assert.Equal(id, back.Id);
        Assert.Equal(ChatRole.Assistant, back.Role);
        Assert.Equal("你好，主人~", back.Content);
        Assert.True(back.IsStreaming);
    }

    [Fact]
    public void ChatRole_HasUserAssistantSystemMembers()
    {
        Assert.Equal(0, (int)ChatRole.User);
        Assert.Equal(1, (int)ChatRole.Assistant);
        Assert.Equal(2, (int)ChatRole.System);
        Assert.True(Enum.IsDefined(typeof(ChatRole), ChatRole.System));
    }

    [Fact]
    public void ISettingsService_ExposesChatAndRagPropertiesWithDefaults()
    {
        var s = new FakeChatSettings();

        Assert.False(s.ChatEnabled);
        Assert.Equal("", s.ChatBaseUrl);
        Assert.Equal("", s.ChatApiKey);
        Assert.Equal("", s.ChatModel);
        Assert.Equal(ISettingsService.DefaultChatSystemPrompt, s.ChatSystemPrompt);
        Assert.Equal(20, s.ChatHistoryCount);
        Assert.Equal(120.0, s.BubbleIntervalSeconds);
        Assert.Equal(120, s.BubbleMaxLength);
        Assert.Equal("", s.EmbeddingModel);
        Assert.False(s.RagEnabled);
    }

    [Fact]
    public void DefaultChatSystemPrompt_MatchesSwiftPersona()
    {
        const string swiftDefault =
            "你是一只住在 macOS 桌面上的可爱宠物猫，名字叫「小桌」。主人是你的好朋友。"
            + "请用简短、温暖、俏皮的口吻像朋友一样闲聊，回复尽量控制在 2-3 句话以内，"
            + "偶尔可以用一两个表情符号（😺🐾✨）。";

        Assert.Equal(swiftDefault, ISettingsService.DefaultChatSystemPrompt);
    }

    [Fact]
    public async Task ChatAndEmbeddingInterfaces_AreConsumable()
    {
        var chat = new FakeChatClient();
        var embedding = new FakeEmbeddingService();

        var reported = new List<string>();
        var reply = await chat.StreamChatAsync(
            new[] { new ChatMessage(Guid.NewGuid(), ChatRole.User, "hi", IsStreaming: false) },
            "hi",
            new Progress<string>(reported.Add),
            CancellationToken.None);

        Assert.Equal("pong", reply);
        Assert.Contains("pong", reported);

        var vectors = await embedding.EmbedAsync(new[] { "a", "b" }, CancellationToken.None);
        Assert.Equal(2, vectors.Count);
        Assert.Equal(new[] { 0.5f, 0.5f }, vectors[0]);
    }

    private sealed class FakeChatSettings : ISettingsService
    {
        public double PetScale { get; set; } = 1.0;
        public double RoamInterval { get; set; } = 4.0;
        public double StatDecaySpeed { get; set; } = 1.0;
        public double SoundVolume { get; set; } = 0.8;
        public bool MusicDance { get; set; }
        public string SkinDirectory { get; set; } = "";
        public bool ChatEnabled { get; set; }
        public string ChatBaseUrl { get; set; } = "";
        public string ChatApiKey { get; set; } = "";
        public string ChatModel { get; set; } = "";
        public string ChatSystemPrompt { get; set; } = ISettingsService.DefaultChatSystemPrompt;
        public int ChatHistoryCount { get; set; } = 20;
        public double BubbleIntervalSeconds { get; set; } = 120;
        public int BubbleMaxLength { get; set; } = 120;
        public string EmbeddingModel { get; set; } = "";
        public bool RagEnabled { get; set; }
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> history, string userInput,
            IProgress<string>? progress, CancellationToken ct)
        {
            progress?.Report("pong");
            return Task.FromResult("pong");
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var result = new List<float[]>();
            foreach (var _ in texts) result.Add(new[] { 0.5f, 0.5f });
            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }
    }
}
