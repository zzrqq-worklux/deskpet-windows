using DeskPet.Core.Text;

namespace DeskPet.Core.Pet;

public sealed record SkinScanResult
{
    public Dictionary<PetAction, List<List<string>>> VariantUrls { get; init; } = new();
    public Dictionary<PetAction, string> Sounds { get; init; } = new();
    public int FrameCount { get; init; }
}

public static class SkinDirectoryScanner
{
    public static SkinScanResult Scan(string rootDir)
    {
        if (!Directory.Exists(rootDir)) return new SkinScanResult();
        var result = new SkinScanResult();
        var variantDirs = new Dictionary<PetAction, List<(int idx, string path)>>();
        foreach (var d in Directory.GetDirectories(rootDir))
        {
            var name = Path.GetFileName(d);
            var us = name.LastIndexOf('_');
            if (us > 0 && int.TryParse(name[(us + 1)..], out var idx)
                && Enum.TryParse<PetAction>(name[..us], true, out var act))
            {
                if (!variantDirs.TryGetValue(act, out var list)) variantDirs[act] = list = new();
                list.Add((idx, d));
            }
        }
        foreach (var (act, list) in variantDirs)
        {
            var variants = new List<List<string>>();
            foreach (var (_, path) in list.OrderBy(x => x.idx))
            {
                var urls = ScanFrames(path);
                if (urls.Count > 0)
                {
                    variants.Add(urls);
                    result.Sounds.TryAdd(act, ScanSound(path) ?? "");
                }
            }
            if (variants.Count > 0) result.VariantUrls[act] = variants;
        }
        foreach (var act in Enum.GetValues<PetAction>())
        {
            if (result.VariantUrls.ContainsKey(act)) continue;
            var dir = Path.Combine(rootDir, act.FolderName());
            var urls = ScanFrames(dir);
            if (urls.Count > 0)
            {
                result.VariantUrls[act] = new() { urls };
                var s = ScanSound(dir);
                if (s != null) result.Sounds[act] = s;
            }
        }
        result = result with { FrameCount = result.VariantUrls.Values.Sum(v => v.Sum(x => x.Count)) };
        return result;
    }

    private static List<string> ScanFrames(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, NaturalComparer.Instance).ToList()
            : new List<string>();

    private static string? ScanSound(string dir)
    {
        foreach (var ext in new[] { "m4a", "mp3", "wav", "aiff" })
        { var f = Path.Combine(dir, "sound." + ext); if (File.Exists(f)) return f; }
        return null;
    }
}
