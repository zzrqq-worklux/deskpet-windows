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
/// 拖拽抓取点保持（fix round：用户验收发现拖拽时鼠标吸附在宠物左下角）�?/// Core 数学（PetDragGrab：target = cursor �?offset，round-trip 不变�?= 抓取点不漂移�? 
/// 窗口层收尾状态（MouseUp/捕获丢失时清�?_grabOffset）�?/// 拖拽启动点（阈值跨越处）的集成路径由启动验证覆盖（SendInput 探针测量窗口 vs 光标位移差恒定）�?/// </summary>
public class PetDragGrabTests
{
    // MARK: - Core 数学（PetDragGrab，纯 BCL�?
    [Fact]
    public void Offset_subtracts_cursor_minus_window_anchor()
    {
        (double X, double Y) offset = PetDragGrab.Offset(cursor: (200.0, 100.0), windowAnchor: (150.0, 50.0));

        Assert.Equal(50.0, offset.X);
        Assert.Equal(50.0, offset.Y);
    }

    [Fact]
    public void Target_subtracts_offset_from_cursor()
    {
        (double X, double Y) target = PetDragGrab.Target(cursor: (200.0, 100.0), offset: (50.0, 50.0));

        Assert.Equal(150.0, target.X);
        Assert.Equal(50.0, target.Y);
    }

    [Fact]
    public void RoundTrip_grab_point_is_preserved()
    {
        // 不变式：target = cursor − (cursor − anchor) == anchor → 拖拽启动瞬间窗口不跳、抓取点不漂移
        (double X, double Y) anchor = (300.0, 400.0);
        (double X, double Y) cursor = (360.0, 430.0);
        (double X, double Y) offset = PetDragGrab.Offset(cursor, anchor);

        (double X, double Y) target = PetDragGrab.Target(cursor, offset);

        Assert.Equal(anchor.X, target.X);
        Assert.Equal(anchor.Y, target.Y);
    }

    [Fact]
    public void RoundTrip_with_negative_coordinates()
    {
        (double X, double Y) anchor = (-100.0, 300.0);
        (double X, double Y) cursor = (-40.0, 250.0);

        (double X, double Y) target = PetDragGrab.Target(cursor, PetDragGrab.Offset(cursor, anchor));

        Assert.Equal(anchor.X, target.X);
        Assert.Equal(anchor.Y, target.Y);
    }

    [Fact]
    public void Zero_offset_targets_cursor_directly()
    {
        // 无基准位置时的退化行为 = 旧实现（光标锚定底左）
        (double X, double Y) cursor = (120.0, 80.0);

        (double X, double Y) target = PetDragGrab.Target(cursor, (0.0, 0.0));

        Assert.Equal(cursor.X, target.X);
        Assert.Equal(cursor.Y, target.Y);
    }

    [Fact]
    public void RoundTrip_with_fractional_values()
    {
        (double X, double Y) anchor = (99.5, 77.25);
        (double X, double Y) cursor = (155.125, 120.75);

        (double X, double Y) target = PetDragGrab.Target(cursor, PetDragGrab.Offset(cursor, anchor));

        Assert.Equal(anchor.X, target.X, 9);
        Assert.Equal(anchor.Y, target.Y, 9);
    }

    // MARK: - 窗口层收尾（拖拽结束后必须清除抓取偏移）

    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pdg_" + Guid.NewGuid().ToString("N"));
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
        public double DpiScale => 1.0;
    }

    private static void SetField(object target, string name, object? value) =>
        typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static object? GetField(object target, string name) =>
        typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target);

    /// <summary>PetWindow 真实 ctor 需解析 BAML（测试宿主不可行），故用 GetUninitializedObject 跳过（同拖拽捕获丢失测试）�?/summary>
    private static object NewWindow() =>
        RuntimeHelpers.GetUninitializedObject(typeof(PetWindow));

    private static void Fire(object window, string handler)
    {
        var method = typeof(PetWindow).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) throw new InvalidOperationException($"PetWindow.{handler} not implemented (fix missing)");
        object[] args = handler switch
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

    private static (object window, PetManager manager) CreateDraggingWindow(bool hoveringAtHome = false)
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        var skin = fx.LoadAsync().GetAwaiter().GetResult();
        if (skin == null) throw new InvalidOperationException("skin load failed");

        var manager = new PetManager(skin, new FakeSettings(), new FakeAudio(), new FakeMusic(), new FakeScreens());
        manager.StartDrag();
        var window = NewWindow();
        SetField(window, "_manager", manager);
        SetField(window, "_mouseDragActive", true);
        SetField(window, "_leftDownInsideWindow", true);
        SetField(window, "_grabOffset", (10.0, 20.0));   // 抓取偏移已记录
        if (hoveringAtHome)
        {
            manager.LifeState = PetLifeState.AtHome;
            manager.IsHoveringDropZone = true;
        }
        return (window, manager);
    }

    [Fact]
    public void MouseUp_drag_branch_clears_grab_offset_and_ends_drag()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateDraggingWindow();

            Fire(window, "OnMouseLeftButtonUp");

            Assert.Null(GetField(window, "_grabOffset"));
            Assert.False(manager.IsDragging);
            Assert.False((bool)GetField(window, "_mouseDragActive")!);
            Assert.False((bool)GetField(window, "_leftDownInsideWindow")!);
        });
    }

    [Fact]
    public void MouseUp_after_drop_home_still_ends_drag()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateDraggingWindow(hoveringAtHome: true);

            Fire(window, "OnMouseLeftButtonUp");

            Assert.Null(GetField(window, "_grabOffset"));
            // 已进窝（投放区 + AtHome）：ExitNotchDropHover=true（清投放悬停态），
            // 但 EndDrag 无条件执行（Swift 语义）——IsDragging 泄漏会冻结出窝后的宠物
            Assert.False(manager.IsDragging);
            Assert.False(manager.IsHoveringDropZone);
        });
    }

    [Fact]
    public void LostMouseCapture_clears_grab_offset_and_ends_drag()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateDraggingWindow();

            Fire(window, "OnLostMouseCapture");

            Assert.Null(GetField(window, "_grabOffset"));
            Assert.False(manager.IsDragging);
            Assert.False((bool)GetField(window, "_mouseDragActive")!);
            Assert.False((bool)GetField(window, "_leftDownInsideWindow")!);
        });
    }

    [Fact]
    public void MouseUp_when_not_dragging_keeps_no_grab_offset()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateDraggingWindow();
            manager.EndDrag();
            SetField(window, "_mouseDragActive", false);
            SetField(window, "_grabOffset", null);

            Fire(window, "OnMouseLeftButtonUp");

            Assert.Null(GetField(window, "_grabOffset"));
            Assert.False(manager.IsDragging);
        });
    }
}
