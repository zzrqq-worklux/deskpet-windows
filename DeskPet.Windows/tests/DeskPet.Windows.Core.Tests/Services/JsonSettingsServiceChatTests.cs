using System;
using System.IO;
using DeskPet.Core.Services;
using DeskPet.Windows.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Services;

public class JsonSettingsServiceChatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetTests", Guid.NewGuid().ToString("N"));

    private JsonSettingsService CreateService(string fileName = "settings.json")
        => new JsonSettingsService(Path.Combine(_dir, fileName));

    [Fact]
    public void MissingFile_ReturnsAllChatDefaults()
    {
        var s = CreateService();

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
    public void SetThenNewInstanceReadsSameChatValues()
    {
        var file = Path.Combine(_dir, "settings.json");
        var first = new JsonSettingsService(file);
        first.ChatEnabled = true;
        first.ChatBaseUrl = "http://localhost:11434/v1";
        first.ChatApiKey = "sk-test";
        first.ChatModel = "qwen2.5:7b";
        first.ChatSystemPrompt = "你是一只住在显示器里的短尾猫。";
        first.ChatHistoryCount = 8;
        first.BubbleIntervalSeconds = 60.0;
        first.BubbleMaxLength = 200;
        first.EmbeddingModel = "bge-m3";
        first.RagEnabled = true;

        Assert.True(File.Exists(file));

        var second = new JsonSettingsService(file);
        Assert.True(second.ChatEnabled);
        Assert.Equal("http://localhost:11434/v1", second.ChatBaseUrl);
        Assert.Equal("sk-test", second.ChatApiKey);
        Assert.Equal("qwen2.5:7b", second.ChatModel);
        Assert.Equal("你是一只住在显示器里的短尾猫。", second.ChatSystemPrompt);
        Assert.Equal(8, second.ChatHistoryCount);
        Assert.Equal(60.0, second.BubbleIntervalSeconds);
        Assert.Equal(200, second.BubbleMaxLength);
        Assert.Equal("bge-m3", second.EmbeddingModel);
        Assert.True(second.RagEnabled);
    }

    [Fact]
    public void CorruptFile_FallsBackToChatDefaultsWithoutThrowing()
    {
        var file = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "{ this is not valid json !!!");

        var s = new JsonSettingsService(file);

        Assert.False(s.ChatEnabled);
        Assert.Equal("", s.ChatBaseUrl);
        Assert.Equal("", s.ChatApiKey);
        Assert.Equal(ISettingsService.DefaultChatSystemPrompt, s.ChatSystemPrompt);
        Assert.Equal(20, s.ChatHistoryCount);
        Assert.Equal(120.0, s.BubbleIntervalSeconds);
        Assert.Equal(120, s.BubbleMaxLength);
        Assert.Equal("", s.EmbeddingModel);
        Assert.False(s.RagEnabled);
    }

    [Fact]
    public void PartialFile_UsesPresentChatValuesAndDefaultsForMissingKeys()
    {
        var file = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "{\"ChatEnabled\": true, \"ChatHistoryCount\": 5}");

        var s = new JsonSettingsService(file);

        Assert.True(s.ChatEnabled);
        Assert.Equal(5, s.ChatHistoryCount);
        Assert.Equal("", s.ChatBaseUrl);
        Assert.Equal(ISettingsService.DefaultChatSystemPrompt, s.ChatSystemPrompt);
        Assert.Equal(120.0, s.BubbleIntervalSeconds);
        Assert.Equal(120, s.BubbleMaxLength);
        Assert.False(s.RagEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
