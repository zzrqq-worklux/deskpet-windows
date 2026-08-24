using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Chat;
using DeskPet.Core.Pet;
using DeskPet.Core.Services;
using DeskPet.Windows.ViewModels;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Chat;

/// <summary>
/// Task 5 修复轮：BubbleViewModel 对话发送路径单元测试（评审发现 1/2 的回归防护）：
/// (a) 发送给客户端的快照不含流式占位（无重复 user、无空 assistant turn）；
/// (b) 流式进行中禁止重入发送/打开输入，且不污染流状态；
/// (c) 发送后历史按 ChatHistoryCount 裁剪并持久化；
/// (d) Dismiss 取消流式后，迟到的增量回调不得复活气泡文本。
/// VM 不依赖 WPF（无 DispatcherTimer/窗口引用），PetManager 用最小真实皮肤构造（同 T4 模式）。
/// </summary>
public class BubbleViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetVmTests", Guid.NewGuid().ToString("N"));
    private SkinFixture? _skinFx;

    public void Dispose()
    {
        _skinFx?.Dispose();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不掩盖测试结果
        }
    }

    // MARK: - 评审发现 1：请求体组装

    [Fact]
    public async Task SendAsync_PassesHistoryWithoutStreamingPlaceholders()
    {
        var chat = new ImmediateChat();
        var (vm, _, _, _) = await NewVmAsync(chat, seed: new[]
        {
            new ChatMessage(Guid.NewGuid(), ChatRole.User, "上一轮问题", IsStreaming: false),
            new ChatMessage(Guid.NewGuid(), ChatRole.Assistant, "上一轮回答", IsStreaming: false),
        });

        vm.OpenInput();
        vm.InputText = "你好";
        await vm.SendAsync();

        Assert.NotNull(chat.LastHistory);
        Assert.DoesNotContain(chat.LastHistory!, m => m.IsStreaming);   // 空 assistant 占位不得到达客户端
        Assert.Equal(1, chat.LastHistory!.Count(m => m.Content == "你好"));  // user 输入不重复
        Assert.Equal(2, chat.LastHistory.Count(m => m.Role == ChatRole.User));  // 上一轮 + 本轮
        Assert.Equal("你好", chat.LastHistory[^1].Content);              // 本次输入仍在最后
        Assert.Equal("回复", vm.PetText);
        Assert.False(vm.IsStreaming);
    }

    // MARK: - 评审发现 2a：流式重入防护

    [Fact]
    public async Task SendAsync_WhileStreaming_SecondSendAndOpenInputAreNoOps()
    {
        var chat = new BlockingChat();
        var (vm, _, _, _) = await NewVmAsync(chat);

        vm.OpenInput();
        vm.InputText = "第一次";
        var first = vm.SendAsync();
        await chat.Entered.Task;                        // 流式已开始
        Assert.True(vm.IsStreaming);

        vm.OpenInput();                                 // 流式中打开输入 → 忽略
        Assert.False(vm.IsInputActive);

        vm.InputText = "第二次";
        var second = vm.SendAsync();
        await second;                                   // 立即返回（无重入）
        Assert.Equal(1, chat.InvocationCount);          // 客户端只被调用一次
        Assert.True(vm.IsStreaming);                    // 流状态未被第二次发送污染

        vm.Dismiss();
        await first;                                    // 被取消的流走 OCE 收尾
        Assert.False(vm.IsStreaming);
        Assert.False(vm.HasContent);
        Assert.Single(vm.History);                      // 用户消息保留，占位已移除
        Assert.Equal("第一次", vm.History[0].Content);
    }

    // MARK: - 顺序多轮对话不被会话标记误伤（回归：guards 不得含 _chatSessionActive）

    [Fact]
    public async Task SendAsync_SequentialChats_EachExchangeProceeds()
    {
        var chat = new ImmediateChat();
        var (vm, _, _, _) = await NewVmAsync(chat, historyCap: 20);

        for (var i = 1; i <= 3; i++)
        {
            vm.OpenInput();                             // 上一轮定稿后 _chatSessionActive 仍为 true（25s 窗口）
            Assert.True(vm.IsInputActive);
            vm.InputText = $"q{i}";
            await vm.SendAsync();
            Assert.Equal("回复", vm.PetText);
            Assert.False(vm.IsStreaming);
        }

        Assert.Equal(3, chat.LastHistory!.Count(m => m.Role == ChatRole.User && m.Content.StartsWith("q")));
    }

    // MARK: - 评审发现 3：历史上限裁剪

    [Fact]
    public async Task SendAsync_HistoryTrimmedToCapAndPersisted()
    {
        var chat = new ImmediateChat();
        var (vm, _, _, path) = await NewVmAsync(chat, historyCap: 4);

        for (var i = 1; i <= 3; i++)
        {
            vm.OpenInput();
            vm.InputText = $"q{i}";
            await vm.SendAsync();
        }

        Assert.Equal(4, vm.History.Count);
        Assert.Equal(new[] { "q2", "回复", "q3", "回复" }, vm.History.Select(m => m.Content).ToArray());
        Assert.Equal(vm.History, new ChatHistoryStore(path).Load());   // 持久化与内存一致
    }

    // MARK: - 评审发现 2b：Dismiss 后流增量不得复活文本

    [Fact]
    public async Task Dismiss_WhileStreaming_StaleProgressDeltasDoNotResurrectText()
    {
        var chat = new BlockingChat { ReportWhileWaiting = true };
        var (vm, _, _, _) = await NewVmAsync(chat);

        vm.OpenInput();
        vm.InputText = "你好";
        var send = vm.SendAsync();
        await chat.Entered.Task;

        vm.Dismiss();                                   // 取消流式 + 清空内容
        await send;                                     // OCE 收尾
        await Task.Delay(100);                          // 给已排队的迟到增量回调执行机会

        Assert.False(vm.HasContent);
        Assert.Null(vm.PetText);
        Assert.Null(vm.UserText);
        Assert.False(vm.IsStreaming);
    }

    // MARK: - 测试辅助

    private async Task<(BubbleViewModel Vm, FakeSettings Settings, ChatHistoryStore Store, string Path)> NewVmAsync(
        IChatClient chat, int historyCap = 20, IReadOnlyList<ChatMessage>? seed = null)
    {
        _skinFx = new SkinFixture();
        _skinFx.Frames("idle_0", 1);
        var skin = (await _skinFx.LoadAsync())!;
        var settings = new FakeSettings { ChatHistoryCount = historyCap };
        var manager = new PetManager(
            skin, settings, new FakeAudio(), new FakeMusic(), new FakeScreens(new Rect(0, 0, 1920, 1080)));
        var path = Path.Combine(_dir, "chat_history.json");
        var store = new ChatHistoryStore(path);
        if (seed != null) store.Save(seed);
        var vm = new BubbleViewModel(manager, settings, chat, store);
        return (vm, settings, store, path);
    }

    /// <summary>立即返回回复的客户端：捕获发给它的历史快照与输入。</summary>
    private sealed class ImmediateChat : IChatClient
    {
        public List<ChatMessage>? LastHistory;
        public string? LastInput;

        public Task<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> history, string userInput, IProgress<string>? progress, CancellationToken ct)
        {
            LastHistory = history.ToList();
            LastInput = userInput;
            progress?.Report("一");
            progress?.Report("二");
            return Task.FromResult("回复");
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>阻塞直到被取消的客户端：模拟流式进行中；可选边等边上报增量。</summary>
    private sealed class BlockingChat : IChatClient
    {
        public int InvocationCount;
        public bool ReportWhileWaiting;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> history, string userInput, IProgress<string>? progress, CancellationToken ct)
        {
            InvocationCount++;
            Entered.TrySetResult();
            if (ReportWhileWaiting)
            {
                while (!ct.IsCancellationRequested)
                {
                    progress?.Report("增量");
                    await Task.Delay(1);
                }
            }
            else
            {
                while (!ct.IsCancellationRequested) await Task.Delay(5);
            }
            ct.ThrowIfCancellationRequested();
            return "unreachable";
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "dpvmskin_" + Guid.NewGuid().ToString("N"));
        public List<string> KnownPaths { get; } = new();

        public void Frames(string folder, int count)
        {
            for (var i = 1; i <= count; i++)
            {
                var p = Path.GetFullPath(Path.Combine(Dir, folder, $"{i}.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
                KnownPaths.Add(p);
            }
        }

        public Task<PetSkin?> LoadAsync() => PetSkin.LoadAsync(Dir, new FakeDecoder(KnownPaths));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly HashSet<string> _known;

        public FakeDecoder(IEnumerable<string> known) => _known = new HashSet<string>(known);

        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
            => Task.FromResult(_known.Contains(Path.GetFullPath(path)) ? new DecodedFrame(4, 4, new byte[4 * 4 * 4]) : null);
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

    private sealed class FakeAudio : IAudioService
    {
        public void Play(string? path, double volume) { }
        public void Stop() { }
    }

    private sealed class FakeMusic : IMusicService { public bool IsPlaying => false; }

    private sealed class FakeScreens : IScreenInfo
    {
        private readonly Rect _workArea;

        public FakeScreens(Rect workArea) => _workArea = workArea;

        public IReadOnlyList<Rect> WorkAreas => new[] { _workArea };
        public Rect PrimaryWorkArea => _workArea;
        public double DpiScale => 1.0;
    }
}
