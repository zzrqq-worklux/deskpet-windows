using System;
using System.IO;
using System.Linq;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class SkinDirectoryScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skin_" + Guid.NewGuid().ToString("N"));
    private void Mk(string rel) { var p = Path.Combine(_dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }

    [Fact]
    public void Scan_Resolves_Variants_And_Natural_Sort()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/10.png");
        Mk("idle_1/1.png");
        Mk("walk_0/1.png"); Mk("walk_0/2.png");
        var r = SkinDirectoryScanner.Scan(_dir);
        Assert.NotNull(r.VariantUrls[PetAction.Idle]);
        Assert.Equal(2, r.VariantUrls[PetAction.Idle].Count);
        Assert.Equal(new[]{"1.png","2.png","10.png"}, r.VariantUrls[PetAction.Idle][0].Select(p=>Path.GetFileName(p)));
        Assert.Equal(new[]{"1.png","2.png"}, r.VariantUrls[PetAction.Walk][0].Select(p=>Path.GetFileName(p)));
    }
    [Fact]
    public void Scan_Picks_Up_Sound()
    {
        Mk("walk_0/1.png"); Mk("walk_0/sound.m4a");
        var r = SkinDirectoryScanner.Scan(_dir);
        Assert.True(r.Sounds[PetAction.Walk].EndsWith("sound.m4a"));
    }
    [Fact]
    public void Scan_Empty_Dir_Returns_Empty() => Assert.Empty(SkinDirectoryScanner.Scan(_dir).VariantUrls);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
