using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class PetSkinBackfillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skin_" + Guid.NewGuid().ToString("N"));

    private void Mk(string rel)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    private void MkFrames(string folder, int count)
    {
        for (var i = 1; i <= count; i++) Mk($"{folder}/{i}.png");
    }

    private static string[] Rels(string folder, int count)
    {
        var rels = new string[count];
        for (var i = 0; i < count; i++) rels[i] = $"{folder}/{i + 1}.png";
        return rels;
    }

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

    private sealed class GatedDecoder : IImageDecoder
    {
        private readonly Dictionary<string, Func<DecodedFrame?>> _map;
        private readonly int _gateAfterCall;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public GatedDecoder(Dictionary<string, Func<DecodedFrame?>> map, int gateAfterCall) { _map = map; _gateAfterCall = gateAfterCall; }
        public int DecodeCalls => Volatile.Read(ref _calls);
        public void Release() => _gate.TrySetResult();
        public async Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call > _gateAfterCall) await _gate.Task;
            return _map.TryGetValue(path, out var f) ? f() : null;
        }
    }

    private static async Task<PetSkin> LoadSkinAsync(string dir, IImageDecoder decoder)
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

    private static bool HasCachedFrames(PetSkin skin, PetAction action, int variant, int count)
    {
        lock (skin.SyncRoot)
        {
            return skin.Cache.TryGetValue(action, out var variants)
                && variant < variants.Count && variants[variant].Count == count;
        }
    }

    private static bool IsPartial(PetSkin skin, string key)
    {
        lock (skin.SyncRoot) { return skin.PartialLoaded.Contains(key); }
    }

    private static bool IsBackfilling(PetSkin skin, string key)
    {
        lock (skin.SyncRoot) { return skin.Backfilling.Contains(key); }
    }

    [Fact]
    public async Task BackfillIfNeeded_TriggersNearEnd_AndGrowsToFull()
    {
        MkFrames("walk_0", 60);
        var decoder = new FakeDecoder(MapFor(_dir, Rels("walk_0", 60)));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() => IsPartial(skin, "walk_0") && HasCachedFrames(skin, PetAction.Walk, 0, 24));

        skin.BackfillIfNeeded(PetAction.Walk, 0, 19);

        await WaitUntilAsync(() => HasCachedFrames(skin, PetAction.Walk, 0, 48) && !IsBackfilling(skin, "walk_0"));
        Assert.True(IsPartial(skin, "walk_0"));

        skin.BackfillIfNeeded(PetAction.Walk, 0, 40);

        await WaitUntilAsync(() => HasCachedFrames(skin, PetAction.Walk, 0, 60));
        Assert.False(IsPartial(skin, "walk_0"));
    }

    [Fact]
    public async Task BackfillIfNeeded_NoTrigger_MoreThan8FramesFromEnd()
    {
        MkFrames("walk_0", 40);
        var decoder = new FakeDecoder(MapFor(_dir, Rels("walk_0", 40)));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() => IsPartial(skin, "walk_0") && HasCachedFrames(skin, PetAction.Walk, 0, 24));

        var callsBefore = decoder.DecodeCalls;
        skin.BackfillIfNeeded(PetAction.Walk, 0, 4);

        await Task.Delay(200);
        Assert.Equal(callsBefore, decoder.DecodeCalls);
        lock (skin.SyncRoot)
        {
            Assert.Equal(24, skin.Cache[PetAction.Walk][0].Count);
            Assert.False(skin.Backfilling.Contains("walk_0"));
            Assert.True(skin.PartialLoaded.Contains("walk_0"));
        }
    }

    [Fact]
    public async Task BackfillIfNeeded_AlreadyFull_ClearsPartialLoaded()
    {
        MkFrames("walk_0", 40);
        MkFrames("music_0", 40);
        var decoder = new FakeDecoder(MapFor(_dir, Rels("walk_0", 40).Concat(Rels("music_0", 40)).ToArray()));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() => IsPartial(skin, "walk_0") && HasCachedFrames(skin, PetAction.Walk, 0, 24));

        skin.Prefetch(new[] { PetAction.Music });
        await WaitUntilAsync(() => IsPartial(skin, "music_0") && HasCachedFrames(skin, PetAction.Music, 0, 24));

        lock (skin.SyncRoot)
        {
            Assert.False(skin.Cache.ContainsKey(PetAction.Walk));
            Assert.True(skin.PartialLoaded.Contains("walk_0"));
        }

        var frames = await WaitForFramesAsync(skin, PetAction.Walk, 0);
        Assert.Equal(40, frames.Count);
        Assert.True(IsPartial(skin, "walk_0"));

        var callsBefore = decoder.DecodeCalls;
        skin.BackfillIfNeeded(PetAction.Walk, 0, 0);

        await Task.Delay(200);
        Assert.False(IsPartial(skin, "walk_0"));
        Assert.Equal(callsBefore, decoder.DecodeCalls);
    }

    [Fact]
    public async Task MarkPlayed_TruncatesNonCoreToTrailer_AndRemarksPartial()
    {
        MkFrames("walk_0", 40);
        MkFrames("idle_0", 30);
        var decoder = new FakeDecoder(MapFor(_dir, Rels("walk_0", 40).Concat(Rels("idle_0", 30)).ToArray()));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() => IsPartial(skin, "walk_0") && HasCachedFrames(skin, PetAction.Walk, 0, 24));

        skin.BackfillIfNeeded(PetAction.Walk, 0, 19);
        await WaitUntilAsync(() => HasCachedFrames(skin, PetAction.Walk, 0, 40));
        Assert.False(IsPartial(skin, "walk_0"));

        var idleFrames = await WaitForFramesAsync(skin, PetAction.Idle, 0);
        Assert.Equal(30, idleFrames.Count);

        skin.MarkPlayed(PetAction.Idle);

        lock (skin.SyncRoot)
        {
            Assert.Contains(PetAction.Idle, skin.Played);
            Assert.Equal(24, skin.Cache[PetAction.Walk][0].Count);
            Assert.True(skin.PartialLoaded.Contains("walk_0"));
            Assert.Equal(30, skin.Cache[PetAction.Idle][0].Count);
            Assert.False(skin.PartialLoaded.Contains("idle_0"));
        }
    }

    [Fact]
    public async Task TrimToHomeOnly_KeepsOnlyHomeAndFall_AndRestoresAfterExit()
    {
        MkFrames("walk_0", 60);
        MkFrames("music_0", 60);
        MkFrames("fall_0", 30);
        Mk("sleep_0/1.png");
        var decoder = new FakeDecoder(MapFor(_dir,
            Rels("walk_0", 60).Concat(Rels("music_0", 60)).Concat(Rels("fall_0", 30)).Concat(new[] { "sleep_0/1.png" }).ToArray()));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk, PetAction.Music, PetAction.Fall });
        await WaitUntilAsync(() =>
            IsPartial(skin, "walk_0") && IsPartial(skin, "music_0") && IsPartial(skin, "fall_0")
            && HasCachedFrames(skin, PetAction.Walk, 0, 24)
            && HasCachedFrames(skin, PetAction.Music, 0, 24)
            && HasCachedFrames(skin, PetAction.Fall, 0, 24));

        var homeFrames = await WaitForFramesAsync(skin, PetAction.Home);
        Assert.Equal(1, homeFrames.Count);

        skin.TrimToHomeOnly();

        lock (skin.SyncRoot)
        {
            Assert.True(skin.HomeOnlyMode);
            Assert.Empty(skin.Prefetched);
            Assert.Empty(skin.Played);
            Assert.Empty(skin.PartialLoaded);
            Assert.Empty(skin.Backfilling);
            Assert.Equal(new HashSet<PetAction> { PetAction.Home, PetAction.Fall }, skin.Cache.Keys.ToHashSet());
            Assert.Equal(24, skin.Cache[PetAction.Fall][0].Count);
        }

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() =>
        {
            lock (skin.SyncRoot) { return !skin.Loading.Contains("walk_0"); }
        });
        lock (skin.SyncRoot) { Assert.False(skin.Cache.ContainsKey(PetAction.Walk)); }

        skin.ExitHomeOnlyMode();

        lock (skin.SyncRoot)
        {
            Assert.False(skin.HomeOnlyMode);
            Assert.True(skin.PartialLoaded.Contains("fall_0"));
        }

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() => IsPartial(skin, "walk_0") && HasCachedFrames(skin, PetAction.Walk, 0, 24));
    }

    [Fact]
    public async Task ExitHomeOnlyMode_RemarksFallPartialWhenNotComplete()
    {
        MkFrames("fall_0", 40);
        var decoder = new FakeDecoder(MapFor(_dir, Rels("fall_0", 40)));
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Fall });
        await WaitUntilAsync(() => IsPartial(skin, "fall_0") && HasCachedFrames(skin, PetAction.Fall, 0, 24));

        skin.TrimToHomeOnly();
        lock (skin.SyncRoot)
        {
            Assert.True(skin.HomeOnlyMode);
            Assert.Empty(skin.PartialLoaded);
        }

        skin.ExitHomeOnlyMode();
        lock (skin.SyncRoot)
        {
            Assert.False(skin.HomeOnlyMode);
            Assert.True(skin.PartialLoaded.Contains("fall_0"));
            Assert.Equal(24, skin.Cache[PetAction.Fall][0].Count);
        }
    }

    [Fact]
    public async Task HomeOnlyMode_DropsInFlightBackfill()
    {
        MkFrames("walk_0", 40);
        var decoder = new GatedDecoder(MapFor(_dir, Rels("walk_0", 40)), gateAfterCall: 41);
        var skin = await LoadSkinAsync(_dir, decoder);

        skin.Prefetch(new[] { PetAction.Walk });
        await WaitUntilAsync(() => IsPartial(skin, "walk_0") && HasCachedFrames(skin, PetAction.Walk, 0, 24));

        skin.BackfillIfNeeded(PetAction.Walk, 0, 19);
        await WaitUntilAsync(() =>
        {
            lock (skin.SyncRoot) { return skin.Backfilling.Contains("walk_0"); }
        });

        skin.TrimToHomeOnly();
        lock (skin.SyncRoot) { Assert.False(skin.Cache.ContainsKey(PetAction.Walk)); }

        var callsAtTrim = decoder.DecodeCalls;
        decoder.Release();
        await WaitUntilAsync(() => decoder.DecodeCalls >= callsAtTrim + 15);

        await Task.Delay(100);
        lock (skin.SyncRoot) { Assert.False(skin.Cache.ContainsKey(PetAction.Walk)); }
    }

    [Fact]
    public async Task StoreEviction_KeepsCorePrefetchedAndCurrentOnly()
    {
        MkFrames("walk_0", 40);
        MkFrames("music_0", 40);
        MkFrames("yawn_0", 10);
        MkFrames("dizzy_0", 8);
        var decoder = new FakeDecoder(MapFor(_dir,
            Rels("walk_0", 40).Concat(Rels("music_0", 40)).Concat(Rels("yawn_0", 10)).Concat(Rels("dizzy_0", 8)).ToArray()));
        var skin = await LoadSkinAsync(_dir, decoder);

        var yawnFrames = await WaitForFramesAsync(skin, PetAction.Yawn, 0);
        Assert.Equal(10, yawnFrames.Count);
        lock (skin.SyncRoot) { Assert.True(skin.Cache.ContainsKey(PetAction.Yawn)); }

        skin.Prefetch(new[] { PetAction.Walk, PetAction.Music });
        await WaitUntilAsync(() =>
            IsPartial(skin, "walk_0") && IsPartial(skin, "music_0")
            && HasCachedFrames(skin, PetAction.Walk, 0, 24)
            && HasCachedFrames(skin, PetAction.Music, 0, 24));

        lock (skin.SyncRoot)
        {
            Assert.False(skin.Cache.ContainsKey(PetAction.Yawn));
            Assert.True(skin.Cache.ContainsKey(PetAction.Walk));
            Assert.True(skin.Cache.ContainsKey(PetAction.Music));
        }

        var dizzyFrames = await WaitForFramesAsync(skin, PetAction.Dizzy, 0);
        Assert.Equal(8, dizzyFrames.Count);

        lock (skin.SyncRoot)
        {
            Assert.True(skin.Cache.ContainsKey(PetAction.Dizzy));
            Assert.True(skin.Cache.ContainsKey(PetAction.Walk));
            Assert.True(skin.Cache.ContainsKey(PetAction.Music));
            Assert.False(skin.Cache.ContainsKey(PetAction.Yawn));
            Assert.Equal(24, skin.Cache[PetAction.Walk][0].Count);
        }
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
