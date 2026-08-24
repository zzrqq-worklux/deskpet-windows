using System;
using System.IO;
using System.Linq;
using DeskPet.Core.Chat;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Chat;

/// <summary>
/// Task 5 ChatHistoryStore 测试（Ruling 5）：Load/Save/Trim（cap = ChatHistoryCount），
/// 对应 Swift PetChatManager.persist（493-495）的 suffix 裁剪语义；
/// 损坏/缺失文件回退空历史不抛异常（与 JsonSettingsService.Load 同策略）。
/// </summary>
public class ChatHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不掩盖测试结果
        }
    }

    private ChatHistoryStore CreateStore(string fileName = "chat_history.json")
        => new(Path.Combine(_dir, fileName));

    private static ChatMessage Msg(ChatRole role, string content, bool streaming = false, Guid? id = null)
        => new(id ?? Guid.NewGuid(), role, content, streaming);

    // MARK: - 持久化 round-trip

    [Fact]
    public void Save_ThenNewInstanceLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(_dir, "chat_history.json");
        new ChatHistoryStore(path).Save(new[]
        {
            Msg(ChatRole.User, "你好呀，今天工作累吗？"),
            Msg(ChatRole.Assistant, "还好啦～ 有你在就不累 😺"),
            Msg(ChatRole.System, "你是一只住在显示器里的短尾猫。"),
            Msg(ChatRole.Assistant, "Zzz…", streaming: true),
        });

        var loaded = new ChatHistoryStore(path).Load();

        Assert.Equal(4, loaded.Count);
        Assert.Equal("你好呀，今天工作累吗？", loaded[0].Content);
        Assert.Equal(ChatRole.User, loaded[0].Role);
        Assert.Equal("还好啦～ 有你在就不累 😺", loaded[1].Content);
        Assert.Equal(ChatRole.Assistant, loaded[1].Role);
        Assert.Equal(ChatRole.System, loaded[2].Role);
        Assert.Equal("Zzz…", loaded[3].Content);
        Assert.True(loaded[3].IsStreaming);
    }

    [Fact]
    public void Save_CreatesDirectoryAndWritesFile()
    {
        var path = Path.Combine(_dir, "nested", "chat_history.json");
        var store = new ChatHistoryStore(path);

        store.Save(new[] { Msg(ChatRole.User, "hi") });

        Assert.True(File.Exists(path));
        Assert.Contains("hi", File.ReadAllText(path));
    }

    // MARK: - Trim（cap = ChatHistoryCount）

    [Fact]
    public void Trim_KeepsMostRecentCapMessages()
    {
        var messages = new[]
        {
            Msg(ChatRole.User, "1"), Msg(ChatRole.Assistant, "2"), Msg(ChatRole.User, "3"),
            Msg(ChatRole.User, "4"), Msg(ChatRole.Assistant, "5"),
        };

        var trimmed = ChatHistoryStore.Trim(messages, 3);

        Assert.Equal(3, trimmed.Count);
        Assert.Equal("3", trimmed[0].Content);
        Assert.Equal("4", trimmed[1].Content);
        Assert.Equal("5", trimmed[2].Content);
    }

    [Fact]
    public void Trim_CountNotExceedingCap_ReturnsAllMessages()
    {
        var messages = new[] { Msg(ChatRole.User, "1"), Msg(ChatRole.Assistant, "2") };

        var trimmed = ChatHistoryStore.Trim(messages, 5);

        Assert.Equal(2, trimmed.Count);
        Assert.Equal("1", trimmed[0].Content);
        Assert.Equal("2", trimmed[1].Content);
    }

    [Fact]
    public void Trim_CapZeroOrNegative_ReturnsEmpty()
    {
        var messages = new[] { Msg(ChatRole.User, "1") };

        Assert.Empty(ChatHistoryStore.Trim(messages, 0));
        Assert.Empty(ChatHistoryStore.Trim(messages, -1));
    }

    [Fact]
    public void Trim_NullMessages_ReturnsEmpty()
    {
        Assert.Empty(ChatHistoryStore.Trim(null!, 5));
    }

    // MARK: - 容错

    [Fact]
    public void Load_MissingFile_ReturnsEmptyHistory()
    {
        var loaded = CreateStore("does-not-exist.json").Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyWithoutThrowing()
    {
        var path = Path.Combine(_dir, "chat_history.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ this is not valid json !!!");

        var loaded = CreateStore().Load();

        Assert.Empty(loaded);
    }
}
