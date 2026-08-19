using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class PetSkinFrameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skin_" + Guid.NewGuid().ToString("N"));

    private void Mk(string rel)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    private string P(string rel) => Path.GetFullPath(Path.Combine(_dir, rel));

    private static DecodedFrame Solid(int w, int h, byte r, byte g, byte b)
    {
        var buf = new byte[w * h * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = 255;
        }
        return new DecodedFrame(w, h, buf);
    }

    private static Dictionary<string, Func<DecodedFrame?>> MapFor(string dir, params string[] rels)
    {
        var map = new Dictionary<string, Func<DecodedFrame?>>();
        foreach (var rel in rels)
        {
            var path = Path.GetFullPath(Path.Combine(dir, rel));
            map[path] = () => Solid(64, 64, 255, 255, 255);
        }
        return map;
    }

    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly Dictionary<string, Func<DecodedFrame?>> _map;
        private int _calls;
        public FakeDecoder(Dictionary<string, Func<DecodedFrame?>> map) { _map = map; }
        public int DecodeCalls => Volatile.Read(ref _calls);
        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_map.TryGetValue(path, out var f) ? f() : null);
        }
    }

    private static async Task<PetSkin> LoadSkinAsync(string dir, FakeDecoder decoder)
    {
        var skin = await PetSkin.LoadAsync(dir, decoder);
        Assert.NotNull(skin);
        return skin!;
    }

    private static async Task<IReadOnlyList<DecodedFrame>> WaitForFramesAsync(PetSkin skin, PetAction action, int variant = -1, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var frames = await skin.FramesAsync(action, variant);
            if (frames != null && frames.Count > 0)
            {
                if (variant < 0) return frames;
                bool isTarget;
                lock (skin.SyncRoot)
                {
                    isTarget = skin.Cache.TryGetValue(action, out var variants)
                        && variant < variants.Count
                        && ReferenceEquals(variants[variant], frames);
                }
                if (isTarget) return frames;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Timed out waiting for frames of {action} variant {variant}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Timed out waiting for async condition");
    }

    [Fact]
    public async Task FramesAsync_LoadsAndCachesFrames()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/3.png");
        var decoder = new FakeDecoder(MapFor(_dir, "idle_0/1.png", "idle_0/2.png", "idle_0/3.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        var first = await skin.FramesAsync(PetAction.Idle);
        Assert.Null(first);

        var frames = await WaitForFramesAsync(skin, PetAction.Idle);
        Assert.Equal(3, frames.Count);

        var callsAfterLoad = decoder.DecodeCalls;
        var again = await skin.FramesAsync(PetAction.Idle);
        Assert.NotNull(again);
        Assert.Equal(3, again!.Count);
        Assert.Same(frames, again);
        Assert.Equal(callsAfterLoad, decoder.DecodeCalls);
    }

    [Fact]
    public async Task FramesAsync_ReturnsTransitionalVariantWhileLoading()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/3.png");
        Mk("idle_1/1.png"); Mk("idle_1/2.png"); Mk("idle_1/3.png");
        var decoder = new FakeDecoder(MapFor(_dir, "idle_0/1.png", "idle_0/2.png", "idle_0/3.png",
            "idle_1/1.png", "idle_1/2.png", "idle_1/3.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        var v0 = await WaitForFramesAsync(skin, PetAction.Idle, 0);
        var transition = await skin.FramesAsync(PetAction.Idle, 1);
        Assert.Same(v0, transition);

        var v1 = await WaitForFramesAsync(skin, PetAction.Idle, 1);
        Assert.Equal(3, v1.Count);
        Assert.NotSame(v0, v1);
    }

    [Fact]
    public async Task Prefetch_NonIdle_TrailerOnlyAndMarksPartialLoaded()
    {
        for (var i = 1; i <= 40; i++) Mk($"music_0/{i}.png");
        var rels = new string[40];
        for (var i = 0; i < 40; i++) rels[i] = $"music_0/{i + 1}.png";
        var decoder = new FakeDecoder(MapFor(_dir, rels));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Music });

        await WaitUntilAsync(() =>
        {
            lock (skin.SyncRoot)
            {
                return skin.PartialLoaded.Contains("music_0")
                    && skin.Cache.TryGetValue(PetAction.Music, out var variants)
                    && variants.Count > 0 && variants[0].Count > 0;
            }
        });

        lock (skin.SyncRoot)
        {
            Assert.Equal(24, skin.Cache[PetAction.Music][0].Count);
            Assert.True(skin.PartialLoaded.Contains("music_0"));
        }
        Assert.Equal(40, skin.TotalFrameCount(PetAction.Music, 0));
    }

    [Fact]
    public async Task Prefetch_Idle_PrefetchesAllVariants()
    {
        for (var i = 1; i <= 30; i++) { Mk($"idle_0/{i}.png"); Mk($"idle_1/{i}.png"); }
        var rels = new string[60];
        for (var i = 0; i < 30; i++) { rels[i] = $"idle_0/{i + 1}.png"; rels[30 + i] = $"idle_1/{i + 1}.png"; }
        var decoder = new FakeDecoder(MapFor(_dir, rels));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Idle });

        await WaitUntilAsync(() =>
        {
            lock (skin.SyncRoot)
            {
                return skin.PartialLoaded.Contains("idle_0") && skin.PartialLoaded.Contains("idle_1")
                    && skin.Cache.TryGetValue(PetAction.Idle, out var variants)
                    && variants.Count >= 2 && variants[0].Count > 0 && variants[1].Count > 0;
            }
        });

        lock (skin.SyncRoot)
        {
            Assert.True(skin.Cache[PetAction.Idle][0].Count <= 24);
            Assert.True(skin.Cache[PetAction.Idle][1].Count <= 24);
        }
        Assert.Equal(30, skin.TotalFrameCount(PetAction.Idle, 0));
    }

    [Fact]
    public async Task Prefetch_NonIdle_OnlyFirstVariant()
    {
        Mk("walk_0/1.png"); Mk("walk_0/2.png"); Mk("walk_0/3.png");
        Mk("walk_1/1.png"); Mk("walk_1/2.png"); Mk("walk_1/3.png");
        var decoder = new FakeDecoder(MapFor(_dir, "walk_0/1.png", "walk_0/2.png", "walk_0/3.png",
            "walk_1/1.png", "walk_1/2.png", "walk_1/3.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk });

        await WaitUntilAsync(() =>
        {
            lock (skin.SyncRoot)
            {
                return skin.Cache.TryGetValue(PetAction.Walk, out var variants)
                    && variants.Count > 0 && variants[0].Count > 0;
            }
        });

        lock (skin.SyncRoot)
        {
            Assert.Equal(3, skin.Cache[PetAction.Walk][0].Count);
            Assert.True(skin.Cache[PetAction.Walk].Count < 2);
        }
    }

    [Fact]
    public async Task PreferredVariant_PrefersCachedVariant()
    {
        Mk("idle_0/1.png"); Mk("idle_1/1.png");
        var decoder = new FakeDecoder(MapFor(_dir, "idle_0/1.png", "idle_1/1.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        await WaitForFramesAsync(skin, PetAction.Idle, 0);

        Assert.Equal(0, skin.PreferredVariant(PetAction.Idle));
    }

    [Fact]
    public async Task PreferredVariant_Uncached_ReturnsValidIndex()
    {
        Mk("walk_0/1.png"); Mk("walk_1/1.png"); Mk("walk_2/1.png");
        var decoder = new FakeDecoder(MapFor(_dir, "walk_0/1.png", "walk_1/1.png", "walk_2/1.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        Assert.InRange(skin.PreferredVariant(PetAction.Walk)!.Value, 0, 2);
    }

    [Fact]
    public async Task FramesAsync_Fallback_MusicUsesIdleFrames()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/3.png");
        var decoder = new FakeDecoder(MapFor(_dir, "idle_0/1.png", "idle_0/2.png", "idle_0/3.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        var musicFrames = await WaitForFramesAsync(skin, PetAction.Music);
        var idleFrames = await WaitForFramesAsync(skin, PetAction.Idle);

        Assert.Same(idleFrames, musicFrames);
    }

    [Fact]
    public async Task FramesAsync_Fallback_HomeUsesSleepLastFrame()
    {
        Mk("sleep/1.png"); Mk("sleep/2.png"); Mk("sleep/3.png");
        var decoder = new FakeDecoder(MapFor(_dir, "sleep/1.png", "sleep/2.png", "sleep/3.png"));
        var skin = await LoadSkinAsync(_dir, decoder);

        var homeFrames = await WaitForFramesAsync(skin, PetAction.Home);
        var sleepFrames = await WaitForFramesAsync(skin, PetAction.Sleep);

        Assert.Equal(1, homeFrames.Count);
        Assert.Same(sleepFrames[2], homeFrames[0]);

        lock (skin.SyncRoot)
        {
            Assert.True(skin.Cache.TryGetValue(PetAction.Home, out var variants)
                && variants.Count > 0 && variants[0].Count == 1);
        }
    }

    [Fact]
    public async Task LoopPoint_ComputedFromSimilarFrames()
    {
        Mk("happy_0/1.png"); Mk("happy_0/2.png"); Mk("happy_0/3.png");
        Mk("happy_0/4.png"); Mk("happy_0/5.png");
        var map = MapFor(_dir, "happy_0/1.png", "happy_0/2.png", "happy_0/3.png", "happy_0/4.png", "happy_0/5.png");
        map[P("happy_0/1.png")] = () => Solid(64, 64, 0, 0, 0);
        var decoder = new FakeDecoder(map);
        var skin = await LoadSkinAsync(_dir, decoder);

        var frames = await WaitForFramesAsync(skin, PetAction.Happy);
        Assert.Equal(5, frames.Count);

        Assert.Equal(1, skin.LoopPoint(PetAction.Happy, 0));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}