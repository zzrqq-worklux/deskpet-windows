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

    private readonly IReadOnlyDictionary<PetAction, double> _fps;
    private readonly IReadOnlyDictionary<PetAction, int> _loops;
    private readonly IReadOnlyDictionary<PetAction, string?> _sounds;

    private const int ProbeMaxPixel = 300;

    private PetSkin(
        IReadOnlyDictionary<PetAction, IReadOnlyList<List<string>>> variantUrls,
        IReadOnlyDictionary<PetAction, double> fps,
        IReadOnlyDictionary<PetAction, int> loops,
        IReadOnlyDictionary<PetAction, string?> sounds,
        double scale,
        (int Width, int Height)? baseFrameSize,
        int maxPixelSize)
    {
        VariantUrls = variantUrls;
        _fps = fps;
        _loops = loops;
        _sounds = sounds;
        Scale = scale;
        BaseFrameSize = baseFrameSize;
        MaxPixelSize = maxPixelSize;
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
        return new PetSkin(variantUrls, fps, loops, sounds, scale, baseSize, maxPixelSize);
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

    public int LoopPoint(PetAction action, int variant) => 0;
}
