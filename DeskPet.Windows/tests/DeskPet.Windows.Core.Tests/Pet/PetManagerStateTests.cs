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
/// PetManager T3 状态与动作切换测试（Swift PetManager.swift 对照）。
/// 皮肤测试方案：最小真实 skin（临时目录 + 同步 FakeDecoder，Phase 0 既有模式）——
/// PetSkin 是 sealed 类不可 mock，用真实皮肤可完整覆盖 SetAction 里
/// MarkPlayed/Prefetch/PreferredVariant/HasMaterial 的真实语义。
/// 状态驱动（Happiness/LifeState 等）走 PetManager 的 internal set（InternalsVisibleTo）。
/// </summary>
public class PetManagerStateTests
{
    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pmskin_" + Guid.NewGuid().ToString("N"));
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

        public void Sound(string folder) => Mk($"{folder}/sound.m4a");

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
        public List<(string? Path, double Volume)> Plays { get; } = new();
        public int StopCalls { get; private set; }
        public void Play(string? path, double volume) => Plays.Add((path, volume));
        public void Stop() => StopCalls++;
    }

    private sealed class FakeMusic : IMusicService { public bool IsPlaying => false; }

    private sealed class FakeScreens : IScreenInfo
    {
        public IReadOnlyList<Rect> WorkAreas { get; } = new[] { new Rect(0, 0, 1920, 1080) };
        public Rect PrimaryWorkArea => new(0, 0, 1920, 1080);
    }

    private static PetManager NewManager(
        PetSkin skin, FakeSettings? settings = null, FakeAudio? audio = null, FakeMusic? music = null,
        FakeScreens? screens = null, Random? rng = null, Func<Task<PetSkin?>>? reloadSkin = null)
        => new(skin, settings ?? new FakeSettings(), audio ?? new FakeAudio(),
               music ?? new FakeMusic(), screens ?? new FakeScreens(), rng, reloadSkin);

    private static List<string> Track(PetManager pm)
    {
        var changes = new List<string>();
        pm.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");
        return changes;
    }

    [Fact]
    public async Task Constructor_ExposesDefaults()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);

        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.False(pm.FacingLeft);
        Assert.Null(pm.CurrentFrameIndex);
        Assert.Equal(0, pm.DizzyAngle);
        Assert.Equal(100, pm.Happiness);
        Assert.Equal(100, pm.Cleanliness);
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.False(pm.IsDragging);
        Assert.Null(pm.WindowPosition);
        Assert.Equal(1.0, pm.WindowAlpha);
    }

    [Fact]
    public async Task SetAction_SwitchesAction_SelectsCachedVariant_FiresPropertyChanged()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 3);
        fx.Frames("idle_1", 3);
        fx.Frames("happy_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 1);
        var pm = NewManager(skin);
        var changes = Track(pm);

        pm.SetAction(PetAction.Happy);
        Assert.Equal(PetAction.Happy, pm.Action);
        Assert.Contains("Action", changes);
        Assert.Equal(0, pm.CurrentVariant);

        pm.SetAction(PetAction.Idle);
        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Equal(1, pm.CurrentVariant);          // 已缓存变体优先

        pm.Tick(1);
        Assert.Equal(0, pm.CurrentFrameIndex);       // 有帧可播
        Assert.Contains("CurrentFrameIndex", changes);

        pm.SetAction(PetAction.Happy);
        Assert.Null(pm.CurrentFrameIndex);           // 动作切换 → 无帧
        Assert.Contains("CurrentFrameIndex", changes);

        pm.SetAction(PetAction.Happy, forceReshuffle: true);
        Assert.Equal(PetAction.Happy, pm.Action);    // 同动作重入 → 循环复位
        Assert.Null(pm.CurrentFrameIndex);
    }

    [Fact]
    public async Task Tick_AdvancesLoop_ByConfiguredFps_AndWraps()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin, rng: new Random(2));   // T4 行为 AI：第 4 tick idleCounter 归零决策；seed 2 roll=0.771≥0.42 → 继续待机，帧循环不被打断
        var seen = new List<int?>();
        pm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pm.CurrentFrameIndex)) seen.Add(pm.CurrentFrameIndex);
        };

        for (var i = 0; i < 7; i++) pm.Tick(1);

        Assert.Equal(new int?[] { 0, 1, 2, 3, 4, 0, 1 }, seen);
        Assert.Equal(1, pm.CurrentFrameIndex);
    }

    [Fact]
    public async Task Tick_ReadsCachedCountEachTick()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);

        pm.Tick(1);                                  // 帧尚未缓存 → 无帧
        Assert.Null(pm.CurrentFrameIndex);

        await WaitCacheAsync(skin, PetAction.Idle, 0);  // 模拟窗口层加载完成

        var seen = new List<int?>();
        pm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pm.CurrentFrameIndex)) seen.Add(pm.CurrentFrameIndex);
        };
        pm.Tick(1);
        pm.Tick(1);

        Assert.Equal(1, pm.CurrentFrameIndex);
        Assert.Equal(new int?[] { 0, 1 }, seen);
    }

    [Fact]
    public async Task Tick_OneShotFinishes_ReturnsToIdle()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 2);
        fx.Frames("happy_0", 5);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Happy, 0);
        var pm = NewManager(skin);
        var seen = new List<int?>();
        pm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pm.CurrentFrameIndex)) seen.Add(pm.CurrentFrameIndex);
        };

        pm.SetAction(PetAction.Happy);
        for (var i = 0; i < 4; i++) pm.Tick(1);
        Assert.Equal(PetAction.Happy, pm.Action);
        Assert.Equal(3, pm.CurrentFrameIndex);

        pm.Tick(1);                                  // 播到末帧 + OneShotFinished → 回 Idle
        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Null(pm.CurrentFrameIndex);
        Assert.Equal(new int?[] { 0, 1, 2, 3, 4, null }, seen);
    }

    [Fact]
    public async Task Fallback_OneShotActionWithoutMaterial_AdvancesViaFallbackAction_ThenCompletes()
    {
        // 皮肤只有 idle（无 fall/drag）：LeaveHome → Fall 走 idle 兜底帧（Swift fallbackFrames 语义），
        // 首帧即算播完（Swift total=0 → total-1=-1）→ 立即回 Idle
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        var seen = new List<int?>();
        pm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pm.CurrentFrameIndex)) seen.Add(pm.CurrentFrameIndex);
        };

        pm.LifeState = PetLifeState.AtHome;
        pm.LeaveHome();                              // → Fall（无真实 fall 素材 → idle 兜底帧）
        Assert.Equal(PetAction.Fall, pm.Action);
        Assert.Null(pm.CurrentFrameIndex);

        pm.Tick(1);                                  // 兜底帧推进：有帧可播 + 播完回 Idle
        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Equal(new int?[] { 0, null }, seen);
    }

    [Fact]
    public async Task Fallback_LoopActionWithoutMaterial_AdvancesViaFallbackAction()
    {
        // 皮肤只有 idle（无 music）：Music 循环走 idle 兜底帧持续推进（Swift fallbackFrames 语义）
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        var pm = NewManager(skin);
        var seen = new List<int?>();
        pm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pm.CurrentFrameIndex)) seen.Add(pm.CurrentFrameIndex);
        };

        pm.SetAction(PetAction.Music);
        for (var i = 0; i < 5; i++) pm.Tick(1);

        Assert.Equal(PetAction.Music, pm.Action);    // 循环兜底不播完
        // Music 无真实素材 → 用 DefaultFps=14（config.fps 只作用于素材存在的动作）→ (int)14t % 3 = 0,2,1,0,2
        Assert.Equal(new int?[] { 0, 2, 1, 0, 2 }, seen);
    }

    [Fact]
    public async Task Fallback_LoopAction_PrefersPrimaryFallbackAction()
    {
        // climb 无素材：兜底链 walk → idle，循环用 walk 的缓存帧数推进（Swift fallbackFrames: Walk ?? Idle）
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 1);
        fx.Frames("walk_0", 3);
        var skin = (await fx.LoadAsync())!;
        await WaitCacheAsync(skin, PetAction.Idle, 0);
        await WaitCacheAsync(skin, PetAction.Walk, 0);
        var pm = NewManager(skin);
        var seen = new List<int?>();
        pm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(pm.CurrentFrameIndex)) seen.Add(pm.CurrentFrameIndex);
        };

        pm.SetAction(PetAction.Climb);
        pm.Tick(1);
        pm.Tick(1);
        pm.Tick(1);

        Assert.Equal(PetAction.Climb, pm.Action);    // 循环兜底不播完
        Assert.Equal(new int?[] { 0, 1, 2 }, seen);  // walk 3 帧（若误用 idle(1 帧) 只会 0）
    }

    [Fact]
    public async Task Poke_WhenHappy_PlaysHappy_AndLowersHappiness()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("happy_0", 1);
        fx.Sound("happy_0");
        var skin = (await fx.LoadAsync())!;
        var audio = new FakeAudio();
        var pm = NewManager(skin, audio: audio);

        pm.Poke();

        Assert.Equal(PetAction.Happy, pm.Action);
        Assert.Equal(99, pm.Happiness);
        var play = Assert.Single(audio.Plays);
        Assert.Equal(fx.P("happy_0/sound.m4a"), play.Path);
        Assert.Equal(0.8, play.Volume);
    }

    [Fact]
    public async Task Poke_WhenSad_PlaysHurt()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("hurt_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.Happiness = 29;

        pm.Poke();

        Assert.Equal(PetAction.Hurt, pm.Action);
        Assert.Equal(29, pm.Happiness);              // hurt 不减心情
    }

    [Fact]
    public async Task Poke_WhileDraggingOrAtHome_IsIgnored()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("happy_0", 1);
        fx.Frames("drag_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);

        pm.StartDrag();
        pm.Poke();
        Assert.Equal(PetAction.Drag, pm.Action);     // 拖拽中不响应

        pm.EndDrag();
        pm.LifeState = PetLifeState.AtHome;
        pm.Poke();
        Assert.Equal(PetAction.Idle, pm.Action);     // 在家不响应
    }

    [Fact]
    public async Task Pet_RecoversHappiness_AndPlaysHappy()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("happy_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.Happiness = 50;

        pm.Pet();
        Assert.Equal(62, pm.Happiness);
        Assert.Equal(PetAction.Happy, pm.Action);

        pm.Pet();                                    // 74
        pm.Pet();                                    // 86
        pm.Pet();                                    // 98
        pm.Pet();                                    // 100（上限）
        Assert.Equal(100, pm.Happiness);
    }

    [Fact]
    public async Task StartDragEndDrag_ToggleState_AndPublish()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("drag_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        var changes = Track(pm);

        pm.StartDrag();
        Assert.True(pm.IsDragging);
        Assert.Equal(PetAction.Drag, pm.Action);
        Assert.Contains("IsDragging", changes);
        Assert.Contains("Action", changes);

        pm.EndDrag();
        Assert.False(pm.IsDragging);
        Assert.Equal(PetAction.Idle, pm.Action);
    }

    [Fact]
    public async Task StartDrag_WhileAtHome_IsRejected()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.LifeState = PetLifeState.AtHome;

        pm.StartDrag();

        Assert.False(pm.IsDragging);
        Assert.Equal(PetAction.Idle, pm.Action);
    }

    [Fact]
    public async Task SetAction_PlaysSoundForActionWithSound_WithSettingsVolume()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("happy_0", 1);
        fx.Sound("happy_0");
        var skin = (await fx.LoadAsync())!;
        var audio = new FakeAudio();
        var settings = new FakeSettings { SoundVolume = 0.6 };
        var pm = NewManager(skin, settings: settings, audio: audio);

        pm.SetAction(PetAction.Happy);

        var play = Assert.Single(audio.Plays);
        Assert.Equal(fx.P("happy_0/sound.m4a"), play.Path);
        Assert.Equal(0.6, play.Volume);
        Assert.Equal(0, audio.StopCalls);
    }

    [Fact]
    public async Task SetAction_IdleAndForceReshuffle_StopSoundWithoutPlaying()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("hurt_0", 1);
        var skin = (await fx.LoadAsync())!;
        var audio = new FakeAudio();
        var pm = NewManager(skin, audio: audio);

        pm.SetAction(PetAction.Hurt);                // 无音效动作 → Play(null) 即停旧声
        Assert.Single(audio.Plays);
        Assert.Null(audio.Plays[0].Path);

        pm.SetAction(PetAction.Idle);                // idle → Stop 不重播
        Assert.Equal(1, audio.StopCalls);
        Assert.Single(audio.Plays);

        pm.SetAction(PetAction.Idle, forceReshuffle: true);  // 变体重入 → Stop
        Assert.Equal(2, audio.StopCalls);
        Assert.Single(audio.Plays);
    }

    [Fact]
    public async Task ReloadSkin_ResetsToIdle()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("happy_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        pm.SetAction(PetAction.Happy);

        pm.ReloadSkin();

        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Null(pm.CurrentFrameIndex);
    }

    [Fact]
    public async Task ReloadSkin_WithProvider_SwapsSkin()
    {
        using var fx1 = new SkinFixture();
        fx1.Frames("idle_0", 2);
        fx1.Frames("idle_1", 2);
        fx1.Frames("happy_0", 2);
        var skin1 = (await fx1.LoadAsync())!;
        await WaitCacheAsync(skin1, PetAction.Idle, 1);

        using var fx2 = new SkinFixture();
        fx2.Frames("idle_0", 2);
        fx2.Frames("happy_0", 2);
        var skin2 = (await fx2.LoadAsync())!;
        await WaitCacheAsync(skin2, PetAction.Idle, 0);

        var providerResult = skin2;
        var pm = NewManager(skin1, reloadSkin: () => Task.FromResult<PetSkin?>(providerResult));

        pm.SetAction(PetAction.Happy);               // 先切走再切回，触发变体选择
        pm.SetAction(PetAction.Idle);
        Assert.Equal(1, pm.CurrentVariant);          // skin1：变体 1 已缓存

        pm.ReloadSkin();
        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Equal(0, pm.CurrentVariant);          // skin2：只有变体 0

        pm.SetAction(PetAction.Happy);
        pm.SetAction(PetAction.Idle);
        Assert.Equal(0, pm.CurrentVariant);
    }

    [Fact]
    public async Task HomeFlow_GuardsAndStateFlips()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("walk_0", 1);
        fx.Frames("fall_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);

        pm.LifeState = PetLifeState.AtHome;
        pm.GoHome();                                 // 拒绝：非 Roaming
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);
        pm.SnapHome();
        Assert.Equal(PetLifeState.AtHome, pm.LifeState);

        pm.LifeState = PetLifeState.Roaming;
        pm.StartDrag();
        pm.GoHome();                                 // 拒绝：拖拽中
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        pm.EndDrag();

        pm.LeaveHome();                              // 拒绝：非 AtHome
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(PetAction.Idle, pm.Action);

        pm.LifeState = PetLifeState.AtHome;
        pm.WindowAlpha = 0.5;
        var changes = Track(pm);
        pm.LeaveHome();                              // 翻转：Roaming + Fall + alpha=1
        Assert.Equal(PetLifeState.Roaming, pm.LifeState);
        Assert.Equal(PetAction.Fall, pm.Action);
        Assert.Equal(1.0, pm.WindowAlpha);
        Assert.Contains("LifeState", changes);
        Assert.Contains("WindowAlpha", changes);
        Assert.Contains("Action", changes);
    }

    [Fact]
    public async Task SyncWindowPosition_UpdatesPosition_OnlyWhenValueProvided()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        var skin = (await fx.LoadAsync())!;
        var pm = NewManager(skin);
        var changes = Track(pm);

        pm.SyncWindowPosition((10.0, 20.0));
        Assert.Equal((10.0, 20.0), pm.WindowPosition!.Value);
        Assert.Contains("WindowPosition", changes);

        pm.SyncWindowPosition(null);                 // null → 保持原位置
        Assert.Equal((10.0, 20.0), pm.WindowPosition!.Value);
    }

    [Fact]
    public async Task Tick_MusicBranch_InactiveWhenNotPlaying()
    {
        using var fx = new SkinFixture();
        fx.Config("{\"fps\": 1}");
        fx.Frames("idle_0", 2);
        fx.Frames("music_0", 2);
        var skin = (await fx.LoadAsync())!;
        var settings = new FakeSettings { MusicDance = true };
        var pm = NewManager(skin, settings: settings);
        await WaitCacheAsync(skin, PetAction.Idle, 0);

        pm.Tick(1);

        Assert.Equal(PetAction.Idle, pm.Action);     // IMusicService 恒 false → 不跳舞
        Assert.Equal(0, pm.CurrentFrameIndex);
    }

    [Fact]
    public async Task Load_Factory_ConstructsAndStarts()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 1);
        fx.Frames("happy_0", 1);
        var skin = (await fx.LoadAsync())!;

        var pm = PetManager.Load(skin, new FakeSettings(), new FakeAudio(), new FakeMusic(), new FakeScreens());

        Assert.Equal(PetAction.Idle, pm.Action);
        Assert.Equal(100, pm.Happiness);
        pm.Poke();
        Assert.Equal(PetAction.Happy, pm.Action);
    }
}
