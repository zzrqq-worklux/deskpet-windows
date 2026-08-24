using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DeskPet.Core.Pet;
using DeskPet.Core.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

/// <summary>
/// PetManager T5 回窝/出窝流程 + 拖拽投放（Windows 托盘重设计）测试
/// （对照 Swift PetManager.swift 281-285、331-357、585-587、610-693、721-731、111-144）。
/// 皮肤方案同 T3/T4：真实最小 skin + 同步 FakeDecoder。
/// TrimToHomeOnly/ExitHomeOnlyMode 观察方式（文档化选择）：
///   - 直接读 internal HomeOnlyMode（InternalsVisibleTo 测试钩子）；
///   - 行为验证：trim 后 idle 帧的加载被 HomeOnlyMode 拒绝（Loading 结束但 Cache 不写入），
///     home 帧仍可加载；ExitHomeOnlyMode 后 idle 帧恢复可加载。
/// 工作区固定 1920x1080（Y=下、y 向上），窗口 64x64：出窝起点 (928, 1216)。
/// 回窝目标 = 距当前位置最近的屏幕边缘（用户设计变更）：左 (X=0)、右 (X=1920−64)、
/// 上 (X=当前X, Y=1080−64−4)；水平==垂直等距时水平优先，左==右等距时左优先。
/// 投放区 = 主屏工作区顶部中央 50% 宽 × 120pt 高矩形：(480, 960)-(1440, 1080)。
/// </summary>
public class PetManagerHomeTests
{
    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pmhskin_" + Guid.NewGuid().ToString("N"));
        public List<string> KnownPaths { get; } = new();

        public string P(string rel) => Path.GetFullPath(Path.Combine(Dir, rel));

        public void Mk(string rel, string content = "x")
        {
            var p = P(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            KnownPaths.Add(p);
        }

        public void Frames(string folder, int count)
        {
            for (var i = 1; i <= count; i++) Mk($"{folder}/{i}.png");
        }

        public void Config(string json) => Mk("config.json", json);

        public Task<PetSkin?> LoadAsync() => PetSkin.LoadAsync(Dir, new FakeDecoder(KnownPaths));

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly HashSet<string> _known;
        public FakeDecoder(IEnumerable<string> known) { _known = new HashSet<string>(known); }
        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
            => Task.FromResult(_known.Contains(Path.GetFullPath(path)) ? new DecodedFrame(64, 64, SolidRgba(64, 64)) : null);
    }

    private static byte[] SolidRgba(int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = 255;
        }
        return buf;
    }

    private static async Task WaitCacheAsync(PetSkin skin, PetAction action, int variant, int minFrames = 1, int timeoutMs = 5000)
    {
        _ = skin.FramesAsync(action, variant);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (skin.SyncRoot)
            {
                if (skin.Cache.TryGetValue(action, out var variants)
                    && variant >= 0 && variant < variants.Count
                    && variants[variant].Count >= minFrames) return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Timed out waiting for {action} variant {variant} cache");
    }

    /// <summary>等待某加载键（如 "idle_0"）结束——无论 StoreLoaded 是写入缓存还是被 HomeOnlyMode 拒绝。</summary>
    private static async Task WaitLoadDoneAsync(PetSkin skin, string key, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (skin.SyncRoot)
            {
                if (!skin.Loading.Contains(key)) return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Timed out waiting for load '{key}' to finish");
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
        private readonly Rect _workArea;
        public FakeScreens(Rect workArea) { _workArea = workArea; }
        public IReadOnlyList<Rect> WorkAreas => new[] { _workArea };
        public Rect PrimaryWorkArea => _workArea;
    }

    private static readonly Rect DefaultWorkArea = new(0, 0, 1920, 1080);

    private static PetManager NewManager(
        PetSkin skin, FakeSettings? settings = null, FakeAudio? audio = null, FakeMusic? music = null,
        FakeScreens? screens = null, Random? rng = null)
        => new(skin, settings ?? new FakeSettings(), audio ?? new FakeAudio(),
               music ?? new FakeMusic(), screens ?? new FakeScreens(DefaultWorkArea), rng);

    private const double Dt = 1.0 / 60.0;
    /// <summary>回窝上边缘目标：工作区顶 − 窗口高 − 4（X 不变时；X=1920/2−32 → 928, 1080−64−4 → 1012）。</summary>
    private static (double X, double Y) HomeTarget => (928.0, 1012.0);
    /// <summary>出窝起点：工作区顶 + 200（1080−64+200）。</summary>
    private static (double X, double Y) LeaveStart => (928.0, 1216.0);
    /// <summary>地面 Y = 工作区底 + 窗口高 6%（与实现同表达式保证位级一致）。</summary>
    private static double FloorY => 64.0 * 0.06;
    /// <summary>投放区内的点（区 = 顶部中央 50%×120pt：(480,960)-(1440,1080)）。</summary>
    private static (double X, double Y) InDropZone => (960.0, 1000.0);

    // MARK: - 最近边缘目标选择（用户设计变更：回窝目标 = 距当前位置最近的屏幕边缘）

    /// <summary>三个区域各取一点：左下 → 左边缘；右中 → 右边缘（窗口右边对齐右边界）；顶中 → 上边缘（X 不变）。</summary>
    [Theory]
    [InlineData(400.0, 300.0, 0.0, 300.0)]     // 左下区域 → 左边缘（X=0 边界，Y 不变）
    [InlineData(1500.0, 540.0, 1856.0, 540.0)]  // 右中区域 → 右边缘（右边界 1920 − 窗口宽 64）
    [InlineData(928.0, 1000.0, 928.0, 1012.0)]  // 顶部中央附近 → 上边缘（沿用现有顶边公式）
    public async Task GoHome_TargetsNearestEdge(double x, double y, double tx, double ty)
    {
        using var fx = new SkinFixture();
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.SyncWindowPosition((x, y));

        pm.GoHome();
        Assert.Equal((tx, ty), pm.WalkTarget!.Value);
        Assert.True(pm.MovingHome);
    }

    /// <summary>恰在等距（水平==垂直）→ 规定水平优先：目标 = 左边缘，Y 不变。</summary>
    [Fact]
    public async Task GoHome_EqualHorizontalAndVerticalDistance_PrefersHorizontalEdge()
    {
        using var fx = new SkinFixture();
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 680.0));   // dLeft=400, dTop=400 → 水平优先 → 左

        pm.GoHome();
        Assert.Equal((0.0, 680.0), pm.WalkTarget!.Value);
    }

    /// <summary>恰在水平居中（左==右==上等距）→ 左优先：目标 = 左边缘。</summary>
    [Fact]
    public async Task GoHome_LeftRightTieAtHorizontalCenter_PrefersLeft()
    {
        using var fx = new SkinFixture();
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.SyncWindowPosition((960.0, 120.0));   // dLeft=dRight=dTop=960 → 水平优先 → 左

        pm.GoHome();
        Assert.Equal((0.0, 120.0), pm.WalkTarget!.Value);
    }

    // MARK: - GoHome：走回 → 淡出 → AtHome + TrimToHomeOnly

    [Fact]
    public async Task GoHome_WalksToNearestEdge_FadesOut_ArrivesAtHome_TrimToHomeOnlyEffective()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"actions\":{\"fall\":{\"fps\":1}}}");
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        fx.Frames("home_0", 3);
        fx.Frames("fall_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Walk, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.GoHome();
        Assert.Equal(PetAction.Walk, pm.Action);
        Assert.True(pm.MovingHome);
        Assert.False(pm.Fading);
        Assert.Equal((0.0, 300.0), pm.WalkTarget!.Value);   // 距 (400,300) 最近 = 左边缘
        Assert.True(pm.FacingLeft);                         // 目标在左侧 → 朝左

        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 2500)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.True(i < 2500, "回窝未在时限内完成（走回 + 淡出）");
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);
        Assert.Equal(PetAction.Home, pm.Action);       // 回窝后播 .home（非 idle）
        Assert.Equal(0.0, pm.WindowAlpha, 9);
        Assert.False(pm.MovingHome);
        Assert.False(pm.Fading);
        Assert.Equal((0.0, 300.0), pm.WindowPosition!.Value);   // 走到左边缘后淡出进窝

        // TrimToHomeOnly 生效：home 帧仍可载入，idle 帧被拒绝
        Assert.True(skin.HomeOnlyMode);
        _ = skin.FramesAsync(PetAction.Home, 0);
        await WaitCacheAsync(skin, PetAction.Home, 0);
        _ = skin.FramesAsync(PetAction.Idle, 0);
        await WaitLoadDoneAsync(skin, "idle_0");
        lock (skin.SyncRoot)
        {
            Assert.False(skin.Cache.ContainsKey(PetAction.Idle));
        }
    }

    // MARK: - SnapHome：直接定位 + 淡出

    [Fact]
    public async Task SnapHome_InstantlyFadesOut_ToAtHome()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("home_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.SnapHome();
        Assert.Equal((0.0, 300.0), pm.WindowPosition!.Value);   // 瞬间定位到最近边缘（左）
        Assert.Equal(PetAction.Idle, pm.Action);              // beginHomeFadeout → Idle
        Assert.True(pm.MovingHome);
        Assert.True(pm.Fading);

        pm.Tick(Dt);                                          // 淡出 −0.06/tick
        Assert.Equal(1.0 - 0.06, pm.WindowAlpha, 9);

        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 100)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.True(i < 100, "淡出未在时限内完成");
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);
        Assert.Equal(PetAction.Home, pm.Action);
        Assert.Equal(0.0, pm.WindowAlpha, 9);
        Assert.True(skin.HomeOnlyMode);
    }

    // MARK: - SnapHome：直接定位到最近边缘 + 淡出

    [Fact]
    public async Task SnapHome_FromRightRegion_SnapsToRightEdge_FadesToAtHome()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("home_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((1700.0, 500.0));

        pm.SnapHome();
        Assert.Equal((1856.0, 500.0), pm.WindowPosition!.Value);   // 右边界 1920 − 窗口宽 64
        Assert.True(pm.Fading);

        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 100)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.True(i < 100, "淡出未在时限内完成");
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);
        Assert.Equal((1856.0, 500.0), pm.WindowPosition!.Value);
    }

    // MARK: - LeaveHome：顶部掉落 → 落地 → Roaming + ExitHomeOnlyMode

    [Fact]
    public async Task LeaveHome_DropsFromTop_Lands_Roaming_ExitHomeOnlyModeEffective()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"actions\":{\"fall\":{\"fps\":1}}}");
        fx.Frames("idle_0", 3);
        fx.Frames("fall_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Fall, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.GoHome();                                          // 先走回进窝 → trim 生效
        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 2500)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);
        Assert.True(skin.HomeOnlyMode);

        pm.LeaveHome();
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(1.0, pm.WindowAlpha);
        Assert.Equal(PetAction.Fall, pm.Action);
        Assert.Equal(PetMovementMode.Falling, pm.MovementMode);
        Assert.False(pm.MovingHome);
        Assert.False(pm.Fading);
        Assert.Equal(LeaveStart, pm.WindowPosition!.Value);   // 工作区顶上方 200pt
        Assert.False(skin.HomeOnlyMode);                      // ExitHomeOnlyMode 已调用

        // ExitHomeOnlyMode 生效：idle 帧恢复可载入
        _ = skin.FramesAsync(PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Idle, 0);

        // 自由落体 → 落地 → Roaming + Idle（fall 动画播完自然过渡）
        var j = 0;
        while (pm.Action != PetAction.Idle && j < 800)
        {
            pm.Tick(Dt);
            j++;
        }
        Assert.True(j < 800, "掉落未在时限内结束");
        Assert.Equal(PetMovementMode.Floor, pm.MovementMode);
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(LeaveStart.X, pm.WindowPosition!.Value.X);
        Assert.Equal(FloorY, pm.WindowPosition.Value.Y);
    }

    // MARK: - 回窝打断：StartDrag / InterruptToFall 复位 movingHome + fading

    [Fact]
    public async Task GoHome_InterruptedByStartDrag_ResetsMovingHomeAndFading()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        fx.Frames("drag_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.GoHome();
        Assert.True(pm.MovingHome);
        Assert.Equal(PetAction.Walk, pm.Action);

        pm.StartDrag();                                       // 走回途中拎起
        Assert.False(pm.MovingHome);                          // movingHome 复位
        Assert.False(pm.Fading);
        Assert.True(pm.IsDragging);
        Assert.Equal(PetAction.Drag, pm.Action);
        Assert.Null(pm.WalkTarget);

        pm.EndDrag();
        pm.Tick(Dt);                                          // 不再走回/淡出/进窝
        Assert.Equal(1.0, pm.WindowAlpha);
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.NotEqual(HomeTarget, pm.WindowPosition!.Value);
    }

    [Fact]
    public async Task SnapHome_FadeInterruptedByStartDrag_FreezeAlphaAndStayRoaming()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);

        pm.SnapHome();
        Assert.True(pm.Fading);
        Assert.True(pm.MovingHome);
        pm.Tick(Dt);
        var alpha = pm.WindowAlpha;
        Assert.True(alpha < 1.0);

        pm.StartDrag();                                       // 淡出途中拎起
        Assert.False(pm.Fading);
        Assert.False(pm.MovingHome);
        Assert.Equal(PetAction.Drag, pm.Action);

        pm.EndDrag();
        pm.Tick(Dt);
        Assert.Equal(alpha, pm.WindowAlpha);                  // 淡出已停，alpha 冻结
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);     // 未进窝
    }

    [Fact]
    public async Task InterruptToFall_ResetsMovingHomeAndFading()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("fall_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Fall, 0);
        var pm = NewManager(skin);

        pm.GoHome();
        Assert.True(pm.MovingHome);

        pm.InterruptToFall();                                 // 路径打断 → 掉落
        Assert.False(pm.MovingHome);
        Assert.False(pm.Fading);
        Assert.Equal(PetMovementMode.Falling, pm.MovementMode);
        Assert.Equal(PetAction.Fall, pm.Action);
    }

    // MARK: - 回窝打断：Poke/Pet 取消回窝过渡（Fix D：回窝途中单击不再飘向边缘）

    [Fact]
    public async Task Poke_DuringGoHomeWalk_CancelsHomeAndPlaysHappy()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Walk, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.GoHome();
        Assert.True(pm.MovingHome);
        Assert.NotNull(pm.WalkTarget);
        Assert.Equal(PetAction.Walk, pm.Action);

        pm.Poke();                                                // 回窝走回途中单击
        Assert.False(pm.MovingHome);                              // 回窝过渡被取消
        Assert.False(pm.Fading);
        Assert.Null(pm.WalkTarget);                               // 走动物理不再推进
        Assert.Equal(PetAction.Happy, pm.Action);

        var pos = pm.WindowPosition!.Value;
        for (var i = 0; i < 10; i++) pm.Tick(Dt);
        Assert.Equal(pos, pm.WindowPosition!.Value);              // 停住，不再飘向边缘
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(1.0, pm.WindowAlpha);
    }

    [Fact]
    public async Task Poke_DuringFade_CancelsHome()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);

        pm.SnapHome();                                            // 瞬间回窝 → 淡出中
        Assert.True(pm.Fading);
        Assert.True(pm.MovingHome);
        pm.Tick(Dt);
        var alpha = pm.WindowAlpha;
        Assert.True(alpha < 1.0);

        pm.Poke();                                                // 淡出途中单击
        Assert.False(pm.Fading);                                  // 淡出被取消
        Assert.False(pm.MovingHome);
        Assert.Equal(PetAction.Happy, pm.Action);

        pm.Tick(Dt);
        Assert.Equal(alpha, pm.WindowAlpha);                      // alpha 冻结，不再继续降
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);         // 未进窝
    }

    [Fact]
    public async Task Pet_DuringGoHomeWalk_CancelsHome()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Walk, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.GoHome();
        Assert.True(pm.MovingHome);
        Assert.NotNull(pm.WalkTarget);

        pm.Pet();                                                 // 回窝走回途中抚摸
        Assert.False(pm.MovingHome);
        Assert.False(pm.Fading);
        Assert.Null(pm.WalkTarget);
        Assert.Equal(PetAction.Happy, pm.Action);
        Assert.Equal(100, pm.Happiness);                          // +12 封顶 100（默认 100 → 100）

        var pos = pm.WindowPosition!.Value;
        for (var i = 0; i < 10; i++) pm.Tick(Dt);
        Assert.Equal(pos, pm.WindowPosition!.Value);              // 停住，不再飘向边缘
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(1.0, pm.WindowAlpha);
    }

    [Fact]
    public async Task Poke_NormalRoaming_Unchanged()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.Poke();                                                // 正常 roaming：行为不变
        Assert.Equal(PetAction.Happy, pm.Action);
        Assert.Equal(99, pm.Happiness);                           // 心情减 1
        Assert.False(pm.MovingHome);
        Assert.False(pm.Fading);
        Assert.Null(pm.WalkTarget);
        Assert.Equal((400.0, 300.0), pm.WindowPosition!.Value);
    }

    // MARK: - 回窝中 guard：GoHome/SnapHome/LeaveHome 拒绝

    [Fact]
    public async Task GoHomeOrSnapHome_WhileMovingHome_Rejected()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((400.0, 300.0));

        pm.GoHome();
        var target = pm.WalkTarget!.Value;
        Assert.True(pm.MovingHome);

        pm.GoHome();                                          // 回窝中 → 拒绝
        Assert.Equal(target, pm.WalkTarget!.Value);           // 目标未被重设
        Assert.Equal(PetAction.Walk, pm.Action);

        pm.SnapHome();                                        // 回窝中 → 拒绝
        Assert.False(pm.Fading);
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
    }

    // MARK: - 拖拽投放（Windows 托盘重设计：顶部中央 50%×120pt）

    [Fact]
    public async Task DropZone_Enter_SnapsHome_ExitBeforeFadeCompletes_ReturnsFalse()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((500.0, 500.0));

        pm.EvaluateNotchDropHover(InDropZone);
        Assert.True(pm.IsHoveringDropZone);
        Assert.True(pm.Fading);                               // SnapHome 已触发 → 淡出开始
        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Equal((0.0, 500.0), pm.WindowPosition!.Value);   // 距 (500,500) 最近 = 左边缘

        Assert.False(pm.ExitNotchDropHover());                // 淡出未完成 → 未进窝 → false
        Assert.False(pm.IsHoveringDropZone);

        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 100)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);      // 松手后仍继续淡出进窝
    }

    [Fact]
    public async Task DropZone_OutsidePoint_NoSnap_ExitWithoutHover_ReturnsFalse()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((500.0, 500.0));

        pm.EvaluateNotchDropHover((100.0, 500.0));            // 区外
        Assert.False(pm.IsHoveringDropZone);
        Assert.False(pm.Fading);
        Assert.Equal((500.0, 500.0), pm.WindowPosition!.Value);
        Assert.Equal(1.0, pm.WindowAlpha);

        Assert.False(pm.ExitNotchDropHover());                // 从未悬停 → false
        Assert.False(pm.IsHoveringDropZone);
    }

    [Fact]
    public async Task DropZone_ExitAfterHome_ReturnsTrue()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);

        pm.EvaluateNotchDropHover(InDropZone);                // 拖入 → SnapHome
        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 100)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);

        pm.EvaluateNotchDropHover(InDropZone);                // 已在窝内再悬停：SnapHome guard 拒绝
        Assert.True(pm.IsHoveringDropZone);
        Assert.True(pm.ExitNotchDropHover());                 // 已进窝 → true
        Assert.False(pm.IsHoveringDropZone);
    }

    // MARK: - LifeStateChanged 事件（T8 托盘消费）

    [Fact]
    public async Task LifeStateChanged_FiresOnHomeAndLeaveTransitions()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("fall_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Fall, 0);
        var pm = NewManager(skin);
        var states = new List<PetLifeState>();
        pm.LifeStateChanged += s => states.Add(s);

        pm.SnapHome();
        var i = 0;
        while (pm.LifeState != PetLifeState.AtHome && i < 100)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);
        Assert.Equal(new[] { PetLifeState.AtHome }, states);

        pm.LeaveHome();
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(new[] { PetLifeState.AtHome, PetLifeState.Roaming }, states);
    }
}
