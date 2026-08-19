using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DeskPet.Windows.Core.Tests")]

namespace DeskPet.Core.Pet;

public sealed class PetSkin
{
    internal static readonly IReadOnlySet<PetAction> CoreActions = new HashSet<PetAction>
    {
        PetAction.Idle, PetAction.Home, PetAction.Drag,
        PetAction.Happy, PetAction.Hurt, PetAction.Fall
    };

    internal IReadOnlyDictionary<PetAction, IReadOnlyList<List<string>>> VariantUrls { get; }
    internal double Scale { get; }

    public int MaxPixelSize { get; }
    public (int Width, int Height)? BaseFrameSize { get; }

    internal const int TrailerFrameCount = 24;
    internal const int BackfillBatch = 24;

    private readonly object _lock = new();
    private readonly IImageDecoder _decoder;
    private readonly IReadOnlyDictionary<PetAction, double> _fps;
    private readonly IReadOnlyDictionary<PetAction, int> _loops;
    private readonly IReadOnlyDictionary<PetAction, string?> _sounds;

    internal Dictionary<PetAction, List<List<DecodedFrame>>> Cache { get; } = new();
    internal Dictionary<PetAction, List<List<float[]>>> Signatures { get; } = new();
    internal Dictionary<PetAction, List<int>> LoopPoints { get; } = new();
    internal HashSet<string> Loading { get; } = new();
    internal HashSet<string> PartialLoaded { get; } = new();
    internal HashSet<PetAction> Prefetched { get; } = new();
    internal object SyncRoot => _lock;

    private const int ProbeMaxPixel = 300;

    private PetSkin(
        IReadOnlyDictionary<PetAction, IReadOnlyList<List<string>>> variantUrls,
        IReadOnlyDictionary<PetAction, double> fps,
        IReadOnlyDictionary<PetAction, int> loops,
        IReadOnlyDictionary<PetAction, string?> sounds,
        double scale,
        (int Width, int Height)? baseFrameSize,
        int maxPixelSize,
        IImageDecoder decoder)
    {
        VariantUrls = variantUrls;
        _fps = fps;
        _loops = loops;
        _sounds = sounds;
        Scale = scale;
        BaseFrameSize = baseFrameSize;
        MaxPixelSize = maxPixelSize;
        _decoder = decoder;
    }

    public static async Task<PetSkin?> LoadAsync(string rootDir, IImageDecoder decoder)
    {
        var scan = SkinDirectoryScanner.Scan(rootDir);
        if (scan.VariantUrls.Count == 0) return null;

        var config = new PetSkinConfig();
        var configPath = Path.Combine(rootDir, "config.json");
        if (File.Exists(configPath))
        {
            try { config = PetSkinConfig.Parse(await File.ReadAllTextAsync(configPath)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        var fps = new Dictionary<PetAction, double>();
        var loops = new Dictionary<PetAction, int>();
        var sounds = new Dictionary<PetAction, string?>();
        foreach (var (action, _) in scan.VariantUrls)
        {
            PetActionConfig? actionConfig = null;
            config.Actions?.TryGetValue(action.ToString().ToLowerInvariant(), out actionConfig);
            fps[action] = actionConfig?.Fps ?? config.Fps ?? action.DefaultFps();
            loops[action] = actionConfig?.Loops ?? (action.LoopsByDefault() ? -1 : 1);
            sounds[action] = scan.Sounds.TryGetValue(action, out var s) && !string.IsNullOrEmpty(s) ? s : null;
        }

        var scale = config.Scale ?? 1.0;
        int widest = 0, tallest = 0;
        foreach (var (action, variants) in scan.VariantUrls)
        {
            var first = variants[0][0];
            var frame = await decoder.DecodeAsync(first, ProbeMaxPixel);
            if (frame == null) continue;
            widest = Math.Max(widest, frame.Width);
            tallest = Math.Max(tallest, frame.Height);
        }
        (int Width, int Height)? baseSize = widest > 0 && tallest > 0 ? (widest, tallest) : null;
        var maxPixelSize = Math.Min(300, Math.Max(256, (int)Math.Round(widest * 1.0 * scale * 2.0)));

        var variantUrls = scan.VariantUrls.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<List<string>>)kv.Value);
        return new PetSkin(variantUrls, fps, loops, sounds, scale, baseSize, maxPixelSize, decoder);
    }

    public int VariantCount(PetAction action)
        => VariantUrls.TryGetValue(action, out var v) ? v.Count : 0;

    public int TotalFrameCount(PetAction action, int variant)
        => VariantUrls.TryGetValue(action, out var v) && variant >= 0 && variant < v.Count ? v[variant].Count : 0;

    public bool HasRealMaterial(PetAction action)
        => VariantUrls.TryGetValue(action, out var v) && v.Count > 0 && v[0].Count > 0;

    public bool HasMaterial(PetAction action)
    {
        if (HasRealMaterial(action)) return true;
        return action switch
        {
            PetAction.Music or PetAction.Hang or PetAction.Dizzy or PetAction.Yawn => VariantUrls.ContainsKey(PetAction.Idle),
            PetAction.Climb => VariantUrls.ContainsKey(PetAction.Walk) || VariantUrls.ContainsKey(PetAction.Idle),
            PetAction.Fall => VariantUrls.ContainsKey(PetAction.Drag) || VariantUrls.ContainsKey(PetAction.Idle),
            PetAction.Home => VariantUrls.ContainsKey(PetAction.Sleep) || VariantUrls.ContainsKey(PetAction.Idle),
            _ => false
        };
    }

    public double Fps(PetAction action) => _fps.TryGetValue(action, out var f) ? f : action.DefaultFps();

    public int Loops(PetAction action) => _loops.TryGetValue(action, out var l) ? l : (action.LoopsByDefault() ? -1 : 1);

    public string? Sound(PetAction action)
        => _sounds.TryGetValue(action, out var s) && !string.IsNullOrEmpty(s) ? s : null;

    public int LoopPoint(PetAction action, int variant)
    {
        lock (_lock)
        {
            return LoopPoints.TryGetValue(action, out var pts) && variant >= 0 && variant < pts.Count
                ? pts[variant] : 0;
        }
    }

    /// <summary>获取动作指定变体的帧：已缓存直接返回；有素材未加载则后台解码（本次返回过渡变体或 null）；
    /// 无素材返回兜底动作帧。variant &lt; 0 或越界时随机选一个变体。</summary>
    public async Task<IReadOnlyList<DecodedFrame>?> FramesAsync(PetAction action, int variant = -1)
    {
        lock (_lock)
        {
            var v = ResolveVariant(VariantUrls, action, variant);
            var cached = GetNonEmptyVariant(Cache, action, v);
            if (cached != null) return cached;
            if (VariantUrls.TryGetValue(action, out var variants) && v >= 0 && v < variants.Count && variants[v].Count > 0)
            {
                var key = Key(action, v);
                if (!Loading.Contains(key))
                {
                    Loading.Add(key);
                    StartLoad(action, v, variants[v]);
                }
                var transitional = FirstNonEmptyVariant(Cache, action);
                if (transitional != null) return transitional;
                return null;
            }
        }
        return await FallbackFramesAsync(action);
    }

    /// <summary>预取动作帧：idle 预取全部变体的 trailer 帧，其余动作只预取第一个非空变体（写 slot 0）。</summary>
    public void Prefetch(IReadOnlyList<PetAction> actions)
    {
        lock (_lock)
        {
            Prefetched.Clear();
            foreach (var action in actions) Prefetched.Add(action);
            foreach (var action in actions)
            {
                if (!VariantUrls.TryGetValue(action, out var variants) || variants.Count == 0) continue;
                if (action == PetAction.Idle)
                {
                    for (var vi = 0; vi < variants.Count; vi++)
                    {
                        if (variants[vi].Count == 0) continue;
                        if (GetNonEmptyVariant(Cache, action, vi) != null) continue;
                        var key = Key(action, vi);
                        if (Loading.Contains(key)) continue;
                        Loading.Add(key);
                        StartPartialLoad(action, vi, variants[vi], TrailerFrameCount);
                    }
                }
                else
                {
                    var urls = variants.FirstOrDefault(v => v.Count > 0);
                    if (urls == null) continue;
                    if (GetNonEmptyVariant(Cache, action, 0) != null) continue;
                    var key = Key(action, 0);
                    if (Loading.Contains(key)) continue;
                    Loading.Add(key);
                    StartPartialLoad(action, 0, urls, TrailerFrameCount);
                }
            }
        }
    }

    /// <summary>变体选择：优先随机返回已缓存（非空）变体；全部未缓存时随机返回 [0, variantCount) 内索引。</summary>
    public int? PreferredVariant(PetAction action)
    {
        lock (_lock)
        {
            if (!VariantUrls.TryGetValue(action, out var variants) || variants.Count == 0) return null;
            var cached = new List<int>();
            for (var i = 0; i < variants.Count; i++)
                if (GetNonEmptyVariant(Cache, action, i) != null) cached.Add(i);
            if (cached.Count > 0) return cached[Random.Shared.Next(cached.Count)];
            return Random.Shared.Next(variants.Count);
        }
    }

    /// <summary>全量加载：后台解码全部帧并计算全量签名，完成后写入缓存。</summary>
    internal void StartLoad(PetAction action, int variant, IReadOnlyList<string> urls)
    {
        var maxPixel = MaxPixelSize;
        var key = Key(action, variant);
        _ = Task.Run(async () =>
        {
            var frames = new List<DecodedFrame>();
            var sigs = new List<float[]>(urls.Count);
            try
            {
                foreach (var url in urls)
                {
                    var frame = await _decoder.DecodeAsync(url, maxPixel);
                    if (frame != null)
                    {
                        frames.Add(frame);
                        sigs.Add(FrameSignatureComputer.Compute(frame.Rgba, frame.Width, frame.Height));
                    }
                    else
                    {
                        sigs.Add(ZeroSignature());
                    }
                }
                lock (_lock) { StoreLoaded(action, variant, frames, sigs, key); }
            }
            catch
            {
                lock (_lock) { Loading.Remove(key); }
            }
        });
    }

    /// <summary>部分帧加载：后台解码前 trailer 帧位图 + 计算全部帧签名，完成后写入缓存并标记 partialLoaded。</summary>
    internal void StartPartialLoad(PetAction action, int variant, IReadOnlyList<string> urls, int trailer)
    {
        var maxPixel = MaxPixelSize;
        var key = Key(action, variant);
        var trailerCount = Math.Min(trailer, urls.Count);
        _ = Task.Run(async () =>
        {
            var trailerFrames = new List<DecodedFrame>();
            var allSigs = new List<float[]>(urls.Count);
            try
            {
                var idx = 0;
                foreach (var url in urls)
                {
                    var frame = await _decoder.DecodeAsync(url, maxPixel);
                    if (frame != null)
                    {
                        allSigs.Add(FrameSignatureComputer.Compute(frame.Rgba, frame.Width, frame.Height));
                        if (idx < trailerCount) trailerFrames.Add(frame);
                    }
                    else
                    {
                        allSigs.Add(ZeroSignature());
                    }
                    idx++;
                }
                lock (_lock) { StorePartialLoaded(action, variant, trailerFrames, allSigs, urls.Count, key); }
            }
            catch
            {
                lock (_lock) { Loading.Remove(key); }
            }
        });
    }

    internal void StoreLoaded(PetAction action, int variant, List<DecodedFrame> frames, List<float[]> sigs, string key)
    {
        Loading.Remove(key);
        if (frames.Count == 0) return;
        var (variants, sigList, loopPts) = PrepareSlots(action, variant);
        variants[variant] = frames;
        sigList[variant] = sigs;
        loopPts[variant] = FrameSignatureComputer.ComputeLoopPoint(sigs.ToArray());
        Cache[action] = variants;
        Signatures[action] = sigList;
        LoopPoints[action] = loopPts;
        Evict(action);
    }

    internal void StorePartialLoaded(PetAction action, int variant, List<DecodedFrame> trailerFrames, List<float[]> allSigs, int totalFrames, string key)
    {
        Loading.Remove(key);
        if (trailerFrames.Count == 0) return;
        var (variants, sigList, loopPts) = PrepareSlots(action, variant);
        variants[variant] = trailerFrames;
        sigList[variant] = allSigs;
        loopPts[variant] = FrameSignatureComputer.ComputeLoopPoint(allSigs.ToArray());
        Cache[action] = variants;
        Signatures[action] = sigList;
        LoopPoints[action] = loopPts;
        if (trailerFrames.Count < totalFrames) PartialLoaded.Add(key);
        Evict(action);
    }

    /// <summary>兜底动作帧：home 用 sleep 末帧（无则 idle 首帧）并写入缓存；其余用 idle/walk/drag。</summary>
    private async Task<IReadOnlyList<DecodedFrame>?> FallbackFramesAsync(PetAction action)
    {
        if (action == PetAction.Home)
        {
            var sleepFrames = await FramesAsync(PetAction.Sleep);
            if (sleepFrames != null && sleepFrames.Count > 0)
                return WriteHomeCache(action, sleepFrames[sleepFrames.Count - 1]);
            var idleFrames = await FramesAsync(PetAction.Idle);
            if (idleFrames != null && idleFrames.Count > 0)
                return WriteHomeCache(action, idleFrames[0]);
            return null;
        }
        return action switch
        {
            PetAction.Music => await FramesAsync(PetAction.Idle),
            PetAction.Climb => await FramesAsync(PetAction.Walk) ?? await FramesAsync(PetAction.Idle),
            PetAction.Hang or PetAction.Dizzy or PetAction.Yawn => await FramesAsync(PetAction.Idle),
            PetAction.Fall => await FramesAsync(PetAction.Drag) ?? await FramesAsync(PetAction.Idle),
            _ => null
        };
    }

    private IReadOnlyList<DecodedFrame> WriteHomeCache(PetAction action, DecodedFrame frame)
    {
        lock (_lock)
        {
            var (variants, _, _) = PrepareSlots(action, 0);
            variants[0] = new List<DecodedFrame> { frame };
            Cache[action] = variants;
            return variants[0];
        }
    }

    private (List<List<DecodedFrame>>, List<List<float[]>>, List<int>) PrepareSlots(PetAction action, int variant)
    {
        var variants = Cache.TryGetValue(action, out var v) ? v : new List<List<DecodedFrame>>();
        var sigList = Signatures.TryGetValue(action, out var s) ? s : new List<List<float[]>>();
        var loopPts = LoopPoints.TryGetValue(action, out var l) ? l : new List<int>();
        var target = Math.Max(variants.Count, Math.Max(sigList.Count, loopPts.Count));
        while (variants.Count < target) variants.Add(new List<DecodedFrame>());
        while (sigList.Count < target) sigList.Add(new List<float[]>());
        while (loopPts.Count < target) loopPts.Add(0);
        while (variants.Count <= variant)
        {
            variants.Add(new List<DecodedFrame>());
            sigList.Add(new List<float[]>());
            loopPts.Add(0);
        }
        return (variants, sigList, loopPts);
    }

    /// <summary>缓存淘汰：只保留 coreActions ∪ prefetched ∪ 当前动作。</summary>
    private void Evict(PetAction action)
    {
        var keep = new HashSet<PetAction>(CoreActions) { action };
        keep.UnionWith(Prefetched);
        foreach (var k in Cache.Keys.Where(k => !keep.Contains(k)).ToArray()) Cache.Remove(k);
        foreach (var k in Signatures.Keys.Where(k => !keep.Contains(k)).ToArray()) Signatures.Remove(k);
        foreach (var k in LoopPoints.Keys.Where(k => !keep.Contains(k)).ToArray()) LoopPoints.Remove(k);
    }

    private static string Key(PetAction action, int variant) => $"{action.FolderName()}_{variant}";

    private static int ResolveVariant(IReadOnlyDictionary<PetAction, IReadOnlyList<List<string>>> variantUrls, PetAction action, int requested)
    {
        if (!variantUrls.TryGetValue(action, out var variants) || variants.Count == 0) return 0;
        if (requested >= 0 && requested < variants.Count) return requested;
        return Random.Shared.Next(variants.Count);
    }

    private static List<DecodedFrame>? GetNonEmptyVariant(Dictionary<PetAction, List<List<DecodedFrame>>> cache, PetAction action, int variant)
        => cache.TryGetValue(action, out var variants) && variant >= 0 && variant < variants.Count && variants[variant].Count > 0
            ? variants[variant] : null;

    private static List<DecodedFrame>? FirstNonEmptyVariant(Dictionary<PetAction, List<List<DecodedFrame>>> cache, PetAction action)
        => cache.TryGetValue(action, out var variants) ? variants.FirstOrDefault(v => v.Count > 0) : null;

    private static float[] ZeroSignature() => FrameSignatureComputer.Compute(Array.Empty<byte>(), 0, 0);
}
