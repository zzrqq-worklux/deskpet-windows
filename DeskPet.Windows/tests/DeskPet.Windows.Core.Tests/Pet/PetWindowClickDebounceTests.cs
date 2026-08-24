using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DeskPet.Core.Pet;
using DeskPet.Core.Services;
using DeskPet.Windows.Views;
using Rect = DeskPet.Core.Services.Rect;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

/// <summary>
/// 双击防抖窗口层状态：单击 → 待定（延迟执行）→ 窗口内第二次单击 → Double（GoHome/LeaveHome）；
/// 计时器超时 → 执行延迟的 Poke；拖拽/捕获丢失/回窝隐藏 → 取消待定。
/// WPF 事件无输入管线无法 headless 驱动（同拖拽测试），用 GetUninitializedObject + 反射调用处理器验证。
/// </summary>
public class PetWindowClickDebounceTests
{
    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pwcd_" + Guid.NewGuid().ToString("N"));
        public List<string> KnownPaths { get; } = new();

        public string P(string rel) => Path.GetFullPath(Path.Combine(Dir, rel));

        public void Mk(string rel)
        {
            var p = P(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
            KnownPaths.Add(p);
        }

        public void Frames(string folder, int count)
        {
            for (var i = 1; i <= count; i++) Mk($"{folder}/{i}.png");
        }

        public Task<PetSkin?> LoadAsync() => PetSkin.LoadAsync(Dir, new FakeDecoder(KnownPaths));

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly HashSet<string> _known;
        public FakeDecoder(IEnumerable<string> known) { _known = new HashSet<string>(known); }
        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
            => Task.FromResult(_known.Contains(Path.GetFullPath(path)) ? new DecodedFrame(8, 8, new byte[8 * 8 * 4]) : null);
    }

    private sealed class FakeSettings : ISettingsService
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

    private sealed class FakeAudio : IAudioService
    {
        public void Play(string? path, double volume) { }
        public void Stop() { }
    }

    private sealed class FakeMusic : IMusicService { public bool IsPlaying => false; }

    private sealed class FakeScreens : IScreenInfo
    {
        public IReadOnlyList<Rect> WorkAreas => new[] { new Rect(0, 0, 1920, 1080) };
        public Rect PrimaryWorkArea => new(0, 0, 1920, 1080);
    }

    private static void SetField(object target, string name, object? value) =>
        typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static object? GetField(object target, string name) =>
        typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target);

    private static object NewWindow() =>
        RuntimeHelpers.GetUninitializedObject(typeof(PetWindow));

    private static void Fire(object window, string handler, object?[]? args = null)
    {
        var method = typeof(PetWindow).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) throw new InvalidOperationException($"PetWindow.{handler} not implemented (fix missing)");
        args ??= handler switch
        {
            "OnMouseLeftButtonUp" => new object[] { new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent } },
            "OnLostMouseCapture" => new object[] { new MouseEventArgs(Mouse.PrimaryDevice, 0) },
            _ => throw new InvalidOperationException($"no args recipe for {handler}")
        };
        method.Invoke(window, args);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static (object window, PetManager manager) CreateWindow()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        var skin = fx.LoadAsync().GetAwaiter().GetResult();
        if (skin == null) throw new InvalidOperationException("skin load failed");

        var manager = new PetManager(skin, new FakeSettings(), new FakeAudio(), new FakeMusic(), new FakeScreens());
        var window = NewWindow();
        SetField(window, "_manager", manager);
        SetField(window, "_leftDownInsideWindow", true);
        return (window, manager);
    }

    [Fact]
    public void First_single_click_arms_pending_instead_of_poking_immediately()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindow();

            Fire(window, "HandleClickUp", new object[] { 1 });

            Assert.True((bool)GetField(window, "_pendingSingleClick")!);   // 待定单击已开启
            Assert.NotEqual(PetAction.Happy, manager.Action);              // 尚未 Poke（延迟执行）
        });
    }

    [Fact]
    public void Second_click_within_window_goes_home_even_with_reset_click_count()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindow();
            Fire(window, "HandleClickUp", new object[] { 1 });   // 第一次单击 → 待定
            Assert.True((bool)GetField(window, "_pendingSingleClick")!);

            // 第二次单击：WPF 因窗口移动把 ClickCount 重置为 1 → 防抖自检测 → Double → GoHome
            Fire(window, "HandleClickUp", new object[] { 1 });

            Assert.False((bool)GetField(window, "_pendingSingleClick")!);
            Assert.Equal(PetAction.Walk, manager.Action);   // GoHome → Walk
            Assert.True(manager.MovingHome);
        });
    }

    [Fact]
    public void Native_double_click_goes_home_directly()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindow();

            Fire(window, "HandleClickUp", new object[] { 2 });

            Assert.Equal(PetAction.Walk, manager.Action);
            Assert.True(manager.MovingHome);
            Assert.False((bool)GetField(window, "_pendingSingleClick")!);
        });
    }

    [Fact]
    public void Timer_expiry_fires_delayed_poke_and_clears_pending()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindow();
            Fire(window, "HandleClickUp", new object[] { 1 });
            Assert.True((bool)GetField(window, "_pendingSingleClick")!);

            Fire(window, "OnSingleClickTimerTick", new object?[] { null, EventArgs.Empty });

            Assert.False((bool)GetField(window, "_pendingSingleClick")!);
            Assert.Equal(PetAction.Happy, manager.Action);   // 延迟的 Poke 已执行（心情 100 → Happy）
        });
    }

    [Fact]
    public void Capture_loss_cancels_pending_single()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindow();
            Fire(window, "HandleClickUp", new object[] { 1 });
            Assert.True((bool)GetField(window, "_pendingSingleClick")!);
            SetField(window, "_mouseDragActive", true);

            Fire(window, "OnLostMouseCapture");

            Assert.False((bool)GetField(window, "_pendingSingleClick")!);
            Assert.False(manager.IsDragging);   // 拖拽收尾正常
        });
    }

    [Fact]
    public void Going_home_cancels_pending_single()
    {
        OnSta(() =>
        {
            // Hide() 在 GetUninitializedObject 的窗口上会抛（DependencyObject 未初始化），
            // 此测试需要真实 Window 实例以覆盖 AtHome → Hide 完整路径（不显示窗口，无 HWND）。
            var window = new PetWindow();
            Fire(window, "HandleClickUp", new object[] { 1 });
            Assert.True((bool)GetField(window, "_pendingSingleClick")!);

            // 宠物回窝隐藏（AtHome → Hide）→ 待定单击取消，重新显示后不会延迟触发 Poke
            Fire(window, "OnLifeStateChanged", new object[] { PetLifeState.AtHome });

            Assert.False((bool)GetField(window, "_pendingSingleClick")!);
        });
    }

    [Fact]
    public void Drag_precedence_still_wins_over_pending_double()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindow();
            Fire(window, "HandleClickUp", new object[] { 1 });
            Assert.True((bool)GetField(window, "_pendingSingleClick")!);
            SetField(window, "_mouseDragActive", true);
            SetField(window, "_leftDownInsideWindow", true);

            // 拖拽中的第二次抬起 → 真实 Up 处理器走拖拽分支（不进点击判定），待定在拖拽结束收尾时清除
            Fire(window, "OnMouseLeftButtonUp");

            Assert.False(manager.IsDragging);
            Assert.False((bool)GetField(window, "_pendingSingleClick")!);
            Assert.NotEqual(PetAction.Walk, manager.Action);   // 未误触发 GoHome
        });
    }
}
