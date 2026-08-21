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
/// T7 fix round：鼠标捕获丢失（窗口隐藏 / 捕获被抢占 / 失焦）时拖拽必须收尾，
/// 否则 manager.IsDragging 永久置位（Poke/Pet/GoHome/StartDrag 全部 no-op，宠物冻结）。
/// 验证窗口层 OnLostMouseCapture / OnLostFocus → FinishDragOnCaptureLoss 语义：
///   1. 拖拽中捕获丢失且未进窝 → EndDrag（IsDragging 复位）；
///   2. 拖拽中捕获丢失但已进窝（投放区 + AtHome）→ ExitNotchDropHover=true，跳过 EndDrag（IsDragging 保持，语义=已回家）；
///   3. LostFocus 路径同 1；
///   4. 两条路径都触发时幂等（只收尾一次）。
/// WPF 事件无法在无 Application/输入管线（testhost 无法解析 /DeskPet.Windows;component 的 BAML）下走
/// 真实窗口与事件，故用 RuntimeHelpers.GetUninitializedObject 跳过 ctor 直接调用受保护的处理器；
/// 事件到处理器的真实接线（Hide() 释放捕获 → LostMouseCapture）由启动验证覆盖。
/// </summary>
public class PetWindowDragCaptureLossTests
{
    // MARK: - 夹具（沿用 T3-T5 最小皮肤模式）

    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pwcl_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>暴露 PetWindow 受保护的捕获丢失/失焦处理器。
    /// 说明：PetWindow 的真实 ctor 会走 InitializeComponent → Application.LoadComponent，
    /// 其 pack URI 依赖 Application.ResourceAssembly（测试宿主=testhost，无法解析
    /// /DeskPet.Windows;component/... 的 BAML）。故用 RuntimeHelpers.GetUninitializedObject
    /// 跳过 ctor（被测处理器不触碰任何 UI 状态），处理器到 WPF 事件的真实接线由启动验证覆盖。</summary>
    private static object NewWindow() =>
        RuntimeHelpers.GetUninitializedObject(typeof(PetWindow));

    private static void Fire(object window, string handler)
    {
        var method = typeof(PetWindow).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) throw new InvalidOperationException($"PetWindow.{handler} not implemented (fix missing)");
        var args = handler == "OnLostMouseCapture"
            ? new object[] { new MouseEventArgs(Mouse.PrimaryDevice, 0) }
            : new object[] { new RoutedEventArgs(UIElement.LostFocusEvent) };
        method.Invoke(window, args);
    }

    private static void SetField(object target, string name, object? value) =>
        typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static bool GetBool(object target, string name) =>
        (bool)typeof(PetWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(target)!;

    /// <summary>WPF 类型需 STA；xUnit 默认 MTA，故每个用例包一层 STA 线程。</summary>
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

    private static (object window, PetManager manager) CreateWindowWithDrag(bool hoveringAtHome = false)
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        var skin = fx.LoadAsync().GetAwaiter().GetResult();
        if (skin == null) throw new InvalidOperationException("skin load failed");

        var manager = new PetManager(skin, new FakeSettings(), new FakeAudio(), new FakeMusic(), new FakeScreens());
        manager.StartDrag();                              // IsDragging=true, action=Drag（拖拽进行中）
        var window = NewWindow();
        SetField(window, "_manager", manager);
        SetField(window, "_mouseDragActive", true);
        SetField(window, "_leftDownInsideWindow", true);
        if (hoveringAtHome)
        {
            manager.LifeState = PetLifeState.AtHome;   // 回窝淡出完成：AtHome
            manager.IsHoveringDropZone = true;         // 拖拽中已进投放区
        }
        return (window, manager);
    }

    // MARK: - LostMouseCapture

    [Fact]
    public void LostMouseCapture_while_dragging_ends_drag()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindowWithDrag();
            Assert.True(manager.IsDragging);

            Fire(window, "OnLostMouseCapture");

            Assert.False(manager.IsDragging);                              // ExitNotchDropHover=false → EndDrag
            Assert.False(GetBool(window, "_mouseDragActive"));
            Assert.False(GetBool(window, "_leftDownInsideWindow"));
        });
    }

    [Fact]
    public void LostMouseCapture_while_dragging_after_home_skips_end_drag()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindowWithDrag(hoveringAtHome: true);

            Fire(window, "OnLostMouseCapture");

            // 已进窝（投放区 + AtHome）：ExitNotchDropHover=true → 跳过 EndDrag（IsDragging 保持=已回家）
            Assert.True(manager.IsDragging);
            Assert.False(manager.IsHoveringDropZone);                        // 投放悬停态被清除
            Assert.False(GetBool(window, "_mouseDragActive"));
            Assert.False(GetBool(window, "_leftDownInsideWindow"));
        });
    }

    [Fact]
    public void LostFocus_while_dragging_ends_drag()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindowWithDrag();

            Fire(window, "OnLostFocus");

            Assert.False(manager.IsDragging);
            Assert.False(GetBool(window, "_mouseDragActive"));
            Assert.False(GetBool(window, "_leftDownInsideWindow"));
        });
    }

    [Fact]
    public void CaptureLoss_and_focus_loss_fired_together_end_drag_once()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindowWithDrag();

            Fire(window, "OnLostMouseCapture");
            Fire(window, "OnLostFocus");    // 第二次被 _mouseDragActive 守卫拦截 → 幂等，不再触达 manager

            Assert.False(manager.IsDragging);
            Assert.False(GetBool(window, "_mouseDragActive"));
            Assert.False(GetBool(window, "_leftDownInsideWindow"));
        });
    }

    [Fact]
    public void LostMouseCapture_when_not_dragging_is_noop()
    {
        OnSta(() =>
        {
            var (window, manager) = CreateWindowWithDrag();
            manager.EndDrag();                       // 正常松手：拖拽已结束
            SetField(window, "_mouseDragActive", false);

            Fire(window, "OnLostMouseCapture");

            Assert.False(manager.IsDragging);        // 不重新触发 EndDrag（无异常、状态不变）
            Assert.False(GetBool(window, "_mouseDragActive"));
        });
    }
}
