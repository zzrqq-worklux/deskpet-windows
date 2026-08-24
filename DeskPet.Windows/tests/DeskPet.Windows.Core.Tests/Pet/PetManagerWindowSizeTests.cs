using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeskPet.Core.Pet;
using DeskPet.Core.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

/// <summary>
/// Fix-A：窗口尺寸 DPI 修复测试（对照 Swift PetManager.swift 69-84 windowSize + minimumWindowHeight=120）。
/// 语义：scale = max(PetScale × 皮肤 scale, 120/max(baseHeight,1))；窗口尺寸 = base × scale × DpiScale（物理像素）。
/// - floor 生效：矮皮肤（64×64）或低 scale 皮肤（BoringPet 300×300 × 0.35）被抬到 120 高；
/// - DpiScale：100% = 1.0，200% = 2.0 时窗口物理像素翻倍（与 Swift 200% 下 240pt 对齐）；
/// - DpiScale 不参与 floor 计算（floor 只由 baseHeight 决定，最终尺寸 = floor 结果 × DpiScale）。
/// </summary>
public class PetManagerWindowSizeTests
{
    // MARK: - 共享公式（PetManager.WindowSizeFor，窗口层同款调用）

    [Fact]
    public void WindowSizeFor_FloorApplies_WhenUserScaleBelowMinimumHeight()
    {
        // BoringPet：300×300 素材 × 皮肤 scale 0.35 → userScale 0.35 < floor 120/300 = 0.4 → 120×120（原 105×105）
        var size = PetManager.WindowSizeFor((300, 300), petScale: 1.0, skinScale: 0.35, dpiScale: 1.0);

        Assert.Equal((120.0, 120.0), size);
    }

    [Fact]
    public void WindowSizeFor_UserScaleAboveFloor_Wins()
    {
        var size = PetManager.WindowSizeFor((300, 300), petScale: 2.0, skinScale: 0.35, dpiScale: 1.0);

        Assert.Equal((210.0, 210.0), size);   // userScale 0.7 > floor 0.4
    }

    [Fact]
    public void WindowSizeFor_FloorAppliesForShortSkin()
    {
        var size = PetManager.WindowSizeFor((64, 64), petScale: 1.0, skinScale: 1.0, dpiScale: 1.0);

        Assert.Equal((120.0, 120.0), size);   // floor 120/64 = 1.875 > userScale 1.0
    }

    [Fact]
    public void WindowSizeFor_DpiScale2_DoublesWindowSize()
    {
        var size = PetManager.WindowSizeFor((300, 300), petScale: 1.0, skinScale: 0.35, dpiScale: 2.0);

        Assert.Equal((240.0, 240.0), size);   // 120 × 2（物理像素，与 Swift 200% 下 240pt 对齐）
    }

    [Fact]
    public void WindowSizeFor_FloorDoesNotScaleWithDpi()
    {
        // floor 主导的矮皮肤：DpiScale 只乘在最终尺寸上 → 2.0 档恰好是 1.0 档的 2 倍（floor 未被 DpiScale 放大）
        var at1 = PetManager.WindowSizeFor((64, 64), petScale: 1.0, skinScale: 1.0, dpiScale: 1.0);
        var at2 = PetManager.WindowSizeFor((64, 64), petScale: 1.0, skinScale: 1.0, dpiScale: 2.0);

        Assert.Equal((120.0, 120.0), at1);
        Assert.Equal((240.0, 240.0), at2);
    }

    [Fact]
    public void WindowSizeFor_NullBaseFrameSize_FallsBackTo120()
    {
        var size = PetManager.WindowSizeFor(null, petScale: 1.0, skinScale: 0.35, dpiScale: 2.0);

        Assert.Equal((120.0, 120.0), size);   // Swift guard-else 语义（baseFrameSize 为 nil → 120×120）
    }

    // MARK: - 行为层验证（经 PetManager 真实位置计算，证明 WindowSize 已接入）

    [Fact]
    public async Task SnapHome_ShortSkin_FloorRaisesWindowToMinimumHeight()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        var pm = new PetManager(skin, new FakeSettings(), new FakeAudio(), new FakeMusic(), new FakeScreens(new Rect(0, 0, 1920, 1080)));
        pm.SyncWindowPosition((960.0, 500.0));   // 距顶 580 < 距左/右 960 → 顶边最近

        pm.SnapHome();

        // 窗口高 = max(64, 120) = 120 → 顶边目标 Y = 1080 − 120 − 4 = 956（floor 前为 1080−64−4=1012）
        Assert.Equal((960.0, 956.0), pm.WindowPosition!.Value);
    }

    [Fact]
    public async Task SnapHome_DpiScale2_DoublesWindowInPhysicalPixels()
    {
        using var fx = new SkinFixture();
        fx.Frames("idle_0", 3);
        var skin = (await fx.LoadAsync())!;
        var pm = new PetManager(skin, new FakeSettings(), new FakeAudio(), new FakeMusic(),
            new FakeScreens(new Rect(0, 0, 1920, 1080), dpiScale: 2.0));
        pm.SyncWindowPosition((960.0, 500.0));

        pm.SnapHome();

        // 200%：窗口 120×2 = 240 物理像素 → 顶边目标 Y = 1080 − 240 − 4 = 836
        Assert.Equal((960.0, 836.0), pm.WindowPosition!.Value);
    }

    // MARK: - 测试桩

    private sealed class SkinFixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pmwskin_" + Guid.NewGuid().ToString("N"));
        public List<string> KnownPaths { get; } = new();

        public string P(string rel) => Path.GetFullPath(Path.Combine(Dir, rel));

        public void Frames(string folder, int count)
        {
            for (var i = 1; i <= count; i++)
            {
                var p = P($"{folder}/{i}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
                KnownPaths.Add(p);
            }
        }

        public Task<PetSkin?> LoadAsync() => PetSkin.LoadAsync(Dir, new FakeDecoder(KnownPaths));

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly HashSet<string> _known;
        public FakeDecoder(IEnumerable<string> known) { _known = new HashSet<string>(known); }
        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
            => Task.FromResult(_known.Contains(Path.GetFullPath(path)) ? new DecodedFrame(64, 64, new byte[64 * 64 * 4]) : null);
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
        private readonly double _dpiScale;
        public FakeScreens(Rect workArea, double dpiScale = 1.0)
        {
            _workArea = workArea;
            _dpiScale = dpiScale;
        }
        public IReadOnlyList<Rect> WorkAreas => new[] { _workArea };
        public Rect PrimaryWorkArea => _workArea;
        public double DpiScale => _dpiScale;
    }
}
