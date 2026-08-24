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
/// PetManager T4 行为层测试：走动/掉落物理 + 行为 AI + 属性衰减
/// （对照 Swift PetManager.swift 257-330、452-605、749-754）。
/// 随机决策用注入固定种子 Random 保证确定性；皮肤为最小真实 skin（同 T3 模式）。
/// 工作区固定 1920x1080（Y=下、y 向上），窗口尺寸 = 64x64（皮肤帧 64x64 × scale 1.0）。
/// </summary>
public class PetManagerBehaviorTests
{
    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pmbskin_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>推进 idleCounter（初始 4s）至归零触发 decideNextBehavior：dt=1 的 4 个 tick。</summary>
    private static void Decide(PetManager pm)
    {
        for (var i = 0; i < 4; i++) pm.Tick(1);
    }

    private const double Dt = 1.0 / 60.0;
    /// <summary>地面 Y = 工作区底 + 窗口高 6% = 0 + 64 * 0.06（与实现同表达式保证位级一致）。</summary>
    private static double FloorY => 64.0 * 0.06;

    // MARK: - 行为 AI（固定种子确定性决策）

    [Fact]
    public async Task BehaviorAI_IdleCounterExpires_WalksDeterministically_WithFixedSeed()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(14));   // seed 14: 第一抽 roll=0.040 < 0.20 → walk
        pm.SyncWindowPosition((400.0, 300.0));

        pm.Tick(1);
        pm.Tick(1);
        pm.Tick(1);                                       // idleCounter 4→1：尚未决策
        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Null(pm.WalkTarget);

        pm.Tick(1);                                       // idleCounter→0 → decideNextBehavior
        Assert.Equal(PetAction.Walk, pm.Action);
        var target = Assert.IsType<(double X, double Y)>(pm.WalkTarget);
        Assert.Equal((400.0, 300.0), pm.WindowPosition!.Value);   // 决策 tick 内不移动
        Assert.Equal(target.X < 400.0, pm.FacingLeft);            // 朝向目标（updateFacing）

        var pm2 = NewManager(skin, rng: new Random(14));  // 同种子 → 完全相同的决策路径
        pm2.SyncWindowPosition((400.0, 300.0));
        Decide(pm2);
        Assert.Equal(pm2.WalkTarget, pm.WalkTarget);
    }

    [Fact]
    public async Task Walk_InterpolatesTowardTarget_ArrivesAndReturnsToIdle()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(14));
        pm.SyncWindowPosition((400.0, 300.0));
        Decide(pm);
        var target = pm.WalkTarget!.Value;
        var before = pm.WindowPosition!.Value;

        pm.Tick(Dt);                                      // 一个 60fps tick：步长 = 70 * (1/60)
        var after = pm.WindowPosition!.Value;
        var moved = Math.Sqrt(Math.Pow(after.X - before.X, 2) + Math.Pow(after.Y - before.Y, 2));
        Assert.Equal(70.0 / 60.0, moved, 6);
        Assert.True(Math.Pow(after.X - target.X, 2) + Math.Pow(after.Y - target.Y, 2)
                    < Math.Pow(before.X - target.X, 2) + Math.Pow(before.Y - target.Y, 2));
        Assert.Equal(PetAction.Walk, pm.Action);

        var i = 0;
        while (pm.Action != PetAction.Idle && i < 3000)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.True(i < 3000, "walk 未在时限内到达目标");
        Assert.Equal(PetAction.Idle, pm.Action);          // 到点 → Idle + reset
        Assert.Null(pm.WalkTarget);
        Assert.Equal(target, pm.WindowPosition!.Value);   // 位置准确
    }

    [Fact]
    public async Task BehaviorAI_Happy_MiniAction_YawnsOnce_ThenReturnsToIdle_NotSleep()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(5));    // roll=0.338∈[0.32,0.42) → mini；mini=0.284<0.5 → yawn（不入睡）
        var seen = new List<PetAction>();

        Decide(pm);
        seen.Add(pm.Action);
        Assert.Equal(PetAction.Yawn, pm.Action);

        var i = 0;
        while (pm.Action != PetAction.Idle && i < 200)
        {
            pm.Tick(Dt);
            if (pm.Action != seen[^1]) seen.Add(pm.Action);
            i++;
        }
        Assert.Equal(PetAction.Idle, pm.Action);          // 独立打哈欠播完 → 回 idle
        Assert.DoesNotContain(PetAction.Sleep, seen);     // 未置 pendingSleep → 不入睡
    }

    [Fact]
    public async Task BehaviorAI_Happy_OtherRolls_StaysIdle()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(2));    // roll=0.771 ≥ 0.42 → 继续待机

        Decide(pm);

        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Null(pm.WalkTarget);
    }

    [Fact]
    public async Task BehaviorAI_Sad_SleepsThroughYawnTransition()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(1));    // roll=0.249 < 0.30（sad 分支）→ sleep
        pm.Happiness = 10;

        Decide(pm);
        Assert.Equal(PetAction.Yawn, pm.Action);          // 先打哈欠

        var i = 0;
        while (pm.Action != PetAction.Sleep && i < 200)
        {
            pm.Tick(Dt);
            i++;
        }
        Assert.Equal(PetAction.Sleep, pm.Action);         // 哈欠播完 → 入睡

        for (var j = 0; j < 60; j++) pm.Tick(Dt);         // sleepHold（5..15s）内保持睡眠
        Assert.Equal(PetAction.Sleep, pm.Action);
    }

    [Fact]
    public async Task SleepPath_YawnCacheEvictedBySleepPrefetch_ReloadsOnDemand_AndTransitionsToSleep()
    {
        // 回归：睡眠预取（Prefetch({Sleep})）替换 Prefetched 集合后，Sleep 帧落地触发 Evict
        // 把正在播放的 Yawn 逐出缓存 → CachedFrameCount=0（有真实素材不落兜底）→ 循环永不推进
        // → 哈欠永不播完 → 永久卡死（无任何重载触发，backfill 也被 PartialLoaded 拦截）。
        // 修复：Tick 帧路径按需重载（FramesAsync 触发 StartLoad），动画恢复后自然过渡到 Sleep。
        using var fx = new SkinFixture();
        fx.Config("{\"actions\":{\"yawn\":{\"fps\":1}}}");
        fx.Frames("idle_0", 3);
        fx.Frames("yawn_0", 5);
        fx.Frames("sleep_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Yawn, 0);    // sleep 不预载：由 SetAction(Yawn) 的 Prefetch({Sleep}) 自然加载
        var pm = NewManager(skin, rng: new Random(1));    // sad → roll 0.249 < 0.30 → startSleeping
        pm.Happiness = 10;

        Decide(pm);                                       // 第 4 tick：StartSleeping → Yawn + pendingSleep + Prefetch({Sleep})
        Assert.Equal(PetAction.Yawn, pm.Action);

        // 睡眠预取帧后台落地 → StorePartialLoaded(Sleep) → Evict：keep=core∪{Sleep}∪{Sleep}，
        // Yawn ∉ keep → Cache[Yawn] 被逐出（bug 触发点）。等待其自然发生。
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && HasCached(skin, PetAction.Yawn, 0)) await Task.Delay(10);
        Assert.False(HasCached(skin, PetAction.Yawn, 0), "前置条件：Yawn 缓存应已被 Evict 逐出");

        // 修复后：Tick 帧路径发现真实素材缓存被逐出 → 按需重载（FramesAsync 触发 StartLoad）。
        // 修复前：无任何加载触发 → 卡死在 Yawn（下方 reloaded 断言即失败）。
        pm.Tick(Dt);                                      // 触发按需重载
        var sw2 = Stopwatch.StartNew();
        while (sw2.ElapsedMilliseconds < 5000 && !HasCached(skin, PetAction.Yawn, 0)) await Task.Delay(10);
        Assert.True(HasCached(skin, PetAction.Yawn, 0), "被逐出的 Yawn 未按需重载（动画永久卡死）");

        // 缓存已恢复：同步推进 → 帧恢复发布 → 哈欠播完 → 自然过渡到 Sleep
        var sawFrameIndex = false;
        var i = 0;
        while (pm.Action != PetAction.Sleep && i < 2000)
        {
            pm.Tick(Dt);
            if (pm.CurrentFrameIndex != null) sawFrameIndex = true;
            i++;
        }
        Assert.True(sawFrameIndex, "Yawn 重载后帧未恢复推进");
        Assert.Equal(PetAction.Sleep, pm.Action);         // 哈欠播完 → 入睡（不再卡在 Yawn）
    }

    private static bool HasCached(PetSkin skin, PetAction action, int variant)
    {
        lock (skin.SyncRoot)
        {
            return skin.Cache.TryGetValue(action, out var variants)
                && variant >= 0 && variant < variants.Count && variants[variant].Count > 0;
        }
    }

    [Fact]
    public async Task BehaviorAI_Sad_Hurts()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(5));    // roll=0.338 ∈ [0.30,0.50)（sad 分支）→ hurt
        pm.Happiness = 10;

        Decide(pm);

        Assert.Equal(PetAction.Hurt, pm.Action);
    }

    [Fact]
    public async Task BehaviorAI_Sad_Walks()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(13));   // roll=0.518 ∈ [0.50,0.70)（sad 分支）→ walk
        pm.Happiness = 10;

        Decide(pm);

        Assert.Equal(PetAction.Walk, pm.Action);
        Assert.NotNull(pm.WalkTarget);
    }

    [Fact]
    public async Task RandomWalkTarget_StaysInSafeZone_AndFarEnoughFromCurrent()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        var minX = 1920.0 * 0.06;
        var maxX = 1920.0 * 0.94 - 64.0;
        var minY = 1080.0 * 0.10;
        var maxY = 1080.0 * 0.60;
        var minDistance = 1920.0 * 0.20;

        for (var seed = 1; seed <= 20; seed++)
        {
            var pm = NewManager(skin, rng: new Random(seed));
            pm.SyncWindowPosition((400.0, 300.0));

            var target = Assert.IsType<(double X, double Y)>(pm.RandomWalkTarget());

            Assert.InRange(target.X, minX - 1e-9, maxX + 1e-9);
            Assert.InRange(target.Y, minY - 1e-9, maxY + 1e-9);
            var d = Math.Sqrt(Math.Pow(target.X - 400.0, 2) + Math.Pow(target.Y - 300.0, 2));
            Assert.True(d >= minDistance - 1e-9, $"seed {seed}: 目标距当前仅 {d}（要求 ≥ {minDistance}）");
        }
    }

    // MARK: - 掉落物理

    [Fact]
    public async Task Drop_InterruptToFall_AcceleratesByGravity_LandsWithoutBounce()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"actions\":{\"fall\":{\"fps\":1}}}");
        fx.Frames("idle_0", 3);
        fx.Frames("fall_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Fall, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((500.0, 19.0));             // 距地面 15.16 → 落地速度 ~123 < 300 → 不弹跳

        pm.InterruptToFall();
        Assert.Equal(PetAction.Fall, pm.Action);
        Assert.Equal(PetMovementMode.Falling, pm.MovementMode);
        Assert.Null(pm.WalkTarget);

        pm.Tick(Dt);                                       // v = g*t
        Assert.Equal(19.0 - 500.0 / 3600.0, pm.WindowPosition!.Value.Y, 9);
        pm.Tick(Dt);                                       // v = 2*g*t：自由落体加速
        Assert.Equal(19.0 - (500.0 + 1000.0) / 3600.0, pm.WindowPosition!.Value.Y, 9);

        var floorY = FloorY;
        var inContact = false;
        var contacts = 0;
        var sawBounce = false;
        var i = 0;
        while (pm.Action != PetAction.Idle && i < 400)
        {
            pm.Tick(Dt);
            var pos = pm.WindowPosition!.Value;
            if (!inContact && pos.Y <= floorY + 1e-9) { inContact = true; contacts++; }
            else if (inContact && pos.Y > floorY + 1e-9) { inContact = false; sawBounce = true; }
            i++;
        }
        Assert.Equal(PetAction.Idle, pm.Action);           // fall 动画播完 → idle
        Assert.Equal(PetMovementMode.Floor, pm.MovementMode);
        Assert.Equal(1, contacts);                         // 慢速落地：一次触地，无弹跳
        Assert.False(sawBounce);
        Assert.Equal(500.0, pm.WindowPosition!.Value.X);
        Assert.Equal(floorY, pm.WindowPosition!.Value.Y);  // 落地位置 = 工作区底 + 窗口高 6%
    }

    [Fact]
    public async Task Drop_LeaveHome_FastLanding_BouncesOnce_ThenSettles()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"actions\":{\"fall\":{\"fps\":1}}}");
        fx.Frames("idle_0", 3);
        fx.Frames("fall_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Fall, 0);
        var pm = NewManager(skin);
        pm.SyncWindowPosition((500.0, 1000.0));           // 高落差 → 落地速度 ~998 > 300 → 反弹 25%
        pm.LifeState = PetLifeState.AtHome;

        pm.LeaveHome();
        Assert.Equal(PetAction.Fall, pm.Action);
        Assert.Equal(PetMovementMode.Falling, pm.MovementMode);

        var floorY = FloorY;
        var inContact = false;
        var contacts = 0;
        var sawBounce = false;
        var i = 0;
        while (pm.Action != PetAction.Idle && i < 500)
        {
            pm.Tick(Dt);
            var pos = pm.WindowPosition!.Value;
            if (!inContact && pos.Y <= floorY + 1e-9) { inContact = true; contacts++; }
            else if (inContact && pos.Y > floorY + 1e-9) { inContact = false; sawBounce = true; }
            i++;
        }
        Assert.True(i < 500, "掉落未在时限内结束");
        Assert.Equal(PetAction.Idle, pm.Action);           // fall 动画播完 → idle
        Assert.Equal(PetMovementMode.Floor, pm.MovementMode);
        Assert.Equal(2, contacts);                         // 高速落地 → 反弹一次 → 再次落地
        Assert.True(sawBounce);
        Assert.Equal(928.0, pm.WindowPosition!.Value.X);   // LeaveHome 定位到工作区顶上方 200pt 中央（Swift 676-679）
        Assert.Equal(floorY, pm.WindowPosition!.Value.Y);  // 最终停在地面
    }

    [Fact]
    public async Task Drop_NoRealFallMaterial_PhysicsStillRuns_ThenCompletesViaFallbackFrames()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(3));    // seed 3：落地回 idle 后的行为决策 roll=0.698≥0.42 → 继续待机，不走动
        pm.SyncWindowPosition((500.0, 19.0));
        pm.LifeState = PetLifeState.AtHome;
        pm.LeaveHome();

        for (var i = 0; i < 300; i++) pm.Tick(Dt);         // 无真实 fall 素材：动画走 idle 兜底帧，物理照常
                                                           // （300 上限：LeaveHome 从顶+200 掉到地面含一次反弹，Swift 676-679 定位）

        Assert.Equal(PetAction.Idle, pm.Action);           // 兜底帧首帧即算播完 → 回 idle（Swift 同）
        Assert.Equal(PetMovementMode.Floor, pm.MovementMode);
        Assert.Equal(928.0, pm.WindowPosition!.Value.X);   // LeaveHome 定位到工作区顶上方 200pt 中央
        Assert.Equal(FloorY, pm.WindowPosition!.Value.Y);  // 已落地并静止
    }

    // MARK: - 属性衰减

    [Fact]
    public async Task StatTick_After60s_CleanlinessDrops_HappinessTendsTo50()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(2));     // 待机种子：决策后继续待机，不影响属性

        pm.Tick(59);                                       // 未满 60s：不衰减
        Assert.Equal(100.0, pm.Cleanliness, 10);
        Assert.Equal(100.0, pm.Happiness, 10);

        pm.Tick(1);                                        // 累计 60s：触发 statTick
        Assert.Equal(100.0 - 0.8, pm.Cleanliness, 10);
        Assert.Equal(100.0 + (50.0 - 100.0) * 0.04, pm.Happiness, 10);

        pm.Tick(60);                                       // 再 60s：cleanliness 再降 0.8，happiness 趋向 50
        Assert.Equal(100.0 - 1.6, pm.Cleanliness, 10);
        Assert.Equal(98.0 + (50.0 - 98.0) * 0.04, pm.Happiness, 10);
    }

    [Fact]
    public async Task StatTick_DecaySpeedScalesCleanlinessDrop()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        var settings = new FakeSettings { StatDecaySpeed = 2.0 };
        var pm = NewManager(skin, settings: settings);

        pm.Tick(60);

        Assert.Equal(100.0 - 1.6, pm.Cleanliness, 10);
    }

    // MARK: - 音乐占位

    [Fact]
    public async Task MusicDanceEnabled_ButIsPlayingFalse_NoDance_AndAIStillRuns()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        fx.Frames("music_0", 3);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var settings = new FakeSettings { MusicDance = true };
        var pm = NewManager(skin, settings: settings, rng: new Random(14));
        pm.SyncWindowPosition((400.0, 300.0));

        Decide(pm);

        Assert.NotEqual(PetAction.Music, pm.Action);       // IsPlaying 恒 false → 音乐分支永不激活
        Assert.Equal(PetAction.Walk, pm.Action);           // 行为 AI 正常运行（未被冻结）

        var before = pm.WindowPosition!.Value;
        pm.Tick(Dt);
        pm.Tick(Dt);
        Assert.NotEqual(before, pm.WindowPosition!.Value); // 走动不受 MusicDance=true 影响
    }
}
