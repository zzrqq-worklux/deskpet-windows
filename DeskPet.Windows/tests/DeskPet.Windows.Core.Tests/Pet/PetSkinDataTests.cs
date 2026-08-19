using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class PetSkinDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skin_" + Guid.NewGuid().ToString("N"));

    private void Mk(string rel)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    private string P(string rel) => Path.GetFullPath(Path.Combine(_dir, rel));

    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly Dictionary<string, (int W, int H)> _dims;
        public FakeDecoder(Dictionary<string, (int W, int H)> dims) { _dims = dims; }
        public int DecodeCalls { get; private set; }
        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
        {
            DecodeCalls++;
            return Task.FromResult<DecodedFrame?>(_dims.TryGetValue(path, out var d)
                ? new DecodedFrame(d.W, d.H, new byte[4]) : null);
        }
    }

    [Fact]
    public async Task LoadAsync_VariantAndFrameCounts()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/3.png");
        Mk("idle_1/1.png");
        Mk("walk_0/1.png"); Mk("walk_0/2.png");
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>()));

        Assert.NotNull(skin);
        Assert.Equal(2, skin!.VariantCount(PetAction.Idle));
        Assert.Equal(1, skin.VariantCount(PetAction.Walk));
        Assert.Equal(0, skin.VariantCount(PetAction.Music));
        Assert.Equal(3, skin.TotalFrameCount(PetAction.Idle, 0));
        Assert.Equal(1, skin.TotalFrameCount(PetAction.Idle, 1));
        Assert.Equal(2, skin.TotalFrameCount(PetAction.Walk, 0));
        Assert.Equal(0, skin.TotalFrameCount(PetAction.Idle, 2));
    }

    [Fact]
    public async Task LoadAsync_ConfigOverridesFpsLoopsScale()
    {
        Mk("idle_0/1.png"); Mk("walk_0/1.png"); Mk("happy_0/1.png");
        Mk("config.json");
        File.WriteAllText(P("config.json"), "{\"fps\":8,\"scale\":1.2,\"actions\":{\"walk\":{\"fps\":5,\"loops\":2}}}");
        var dims = new Dictionary<string, (int, int)>
        {
            [P("idle_0/1.png")] = (120, 120),
            [P("walk_0/1.png")] = (120, 120),
            [P("happy_0/1.png")] = (120, 120),
        };
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(dims));

        Assert.NotNull(skin);
        Assert.Equal(8, skin!.Fps(PetAction.Idle));
        Assert.Equal(5, skin.Fps(PetAction.Walk));
        Assert.Equal(2, skin.Loops(PetAction.Walk));
        Assert.Equal(-1, skin.Loops(PetAction.Idle));
        Assert.Equal(1, skin.Loops(PetAction.Happy));
        Assert.Equal(288, skin.MaxPixelSize);
    }

    [Fact]
    public async Task LoadAsync_DefaultsWithoutConfig()
    {
        Mk("idle_0/1.png"); Mk("walk_0/1.png");
        var dims = new Dictionary<string, (int, int)>
        {
            [P("idle_0/1.png")] = (120, 120),
            [P("walk_0/1.png")] = (200, 120),
        };
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(dims));

        Assert.NotNull(skin);
        Assert.Equal(6, skin!.Fps(PetAction.Idle));
        Assert.Equal(12, skin.Fps(PetAction.Walk));
        Assert.Equal(-1, skin.Loops(PetAction.Walk));
        Assert.Equal((200, 120), skin.BaseFrameSize!.Value);
        Assert.Equal(300, skin.MaxPixelSize);
    }

    [Fact]
    public async Task LoadAsync_BaseFrameSizeIsWidestByTallest()
    {
        Mk("idle_0/1.png"); Mk("walk_0/1.png");
        var dims = new Dictionary<string, (int, int)>
        {
            [P("idle_0/1.png")] = (200, 100),
            [P("walk_0/1.png")] = (120, 300),
        };
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(dims));

        Assert.NotNull(skin);
        Assert.NotNull(skin!.BaseFrameSize);
        Assert.Equal(200, skin.BaseFrameSize!.Value.Width);
        Assert.Equal(300, skin.BaseFrameSize.Value.Height);
    }

    [Fact]
    public async Task LoadAsync_BaseFrameSizeNullWhenNoFrameDecodes()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png");
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>()));

        Assert.NotNull(skin);
        Assert.Null(skin!.BaseFrameSize);
        Assert.Equal(256, skin.MaxPixelSize);
    }

    [Fact]
    public async Task LoadAsync_SoundFromScan()
    {
        Mk("walk_0/1.png"); Mk("walk_0/sound.m4a");
        Mk("sleep/1.png");
        Mk("happy_0/1.png");
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>()));

        Assert.NotNull(skin);
        Assert.EndsWith("sound.m4a", skin!.Sound(PetAction.Walk));
        Assert.Null(skin.Sound(PetAction.Sleep));
        Assert.Null(skin.Sound(PetAction.Happy));
    }

    [Fact]
    public async Task LoadAsync_EmptyOrMissingDirReturnsNull()
    {
        Assert.Null(await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>())));
        Assert.Null(await PetSkin.LoadAsync(P("does_not_exist"), new FakeDecoder(new Dictionary<string, (int, int)>())));
    }

    [Fact]
    public async Task HasMaterial_WithIdleFallback()
    {
        Mk("idle_0/1.png"); Mk("music_0/1.png"); Mk("climb_0/1.png");
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>()));

        Assert.NotNull(skin);
        Assert.True(skin!.HasRealMaterial(PetAction.Idle));
        Assert.True(skin.HasRealMaterial(PetAction.Music));
        Assert.True(skin.HasMaterial(PetAction.Music));
        Assert.True(skin.HasMaterial(PetAction.Climb));
        Assert.True(skin.HasMaterial(PetAction.Home));
        Assert.False(skin.HasMaterial(PetAction.Eat));
        Assert.True(skin.HasMaterial(PetAction.Hang));
        Assert.True(skin.HasMaterial(PetAction.Dizzy));
        Assert.True(skin.HasMaterial(PetAction.Yawn));
    }

    [Fact]
    public async Task HasMaterial_WithoutFallbackSource()
    {
        Mk("music_0/1.png");
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>()));

        Assert.NotNull(skin);
        Assert.True(skin!.HasMaterial(PetAction.Music));
        Assert.False(skin.HasRealMaterial(PetAction.Home));
        Assert.False(skin.HasMaterial(PetAction.Home));
        Assert.False(skin.HasMaterial(PetAction.Climb));
        Assert.False(skin.HasMaterial(PetAction.Fall));
    }

    [Fact]
    public async Task LoopPoint_ReturnsZeroPlaceholder()
    {
        Mk("idle_0/1.png"); Mk("walk_0/1.png");
        var skin = await PetSkin.LoadAsync(_dir, new FakeDecoder(new Dictionary<string, (int, int)>()));

        Assert.NotNull(skin);
        Assert.Equal(0, skin!.LoopPoint(PetAction.Idle, 0));
        Assert.Equal(0, skin.LoopPoint(PetAction.Walk, 5));
    }

    [Fact]
    public async Task LoadAsync_DecodesOnlyFirstFramePerAction()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/3.png");
        Mk("walk_0/1.png"); Mk("walk_0/2.png");
        var decoder = new FakeDecoder(new Dictionary<string, (int, int)>());
        await PetSkin.LoadAsync(_dir, decoder);

        Assert.Equal(2, decoder.DecodeCalls);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
