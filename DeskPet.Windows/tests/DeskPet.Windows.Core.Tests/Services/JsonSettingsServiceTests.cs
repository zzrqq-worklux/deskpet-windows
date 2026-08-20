using System;
using System.IO;
using DeskPet.Windows.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Services;

public class JsonSettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DeskPetTests", Guid.NewGuid().ToString("N"));

    private JsonSettingsService CreateService(string fileName = "settings.json")
        => new JsonSettingsService(Path.Combine(_dir, fileName));

    [Fact]
    public void MissingFile_ReturnsAllDefaults()
    {
        var s = CreateService();

        Assert.Equal(1.0, s.PetScale);
        Assert.Equal(4.0, s.RoamInterval);
        Assert.Equal(1.0, s.StatDecaySpeed);
        Assert.Equal(1.0, s.SoundVolume);
        Assert.False(s.MusicDance);
        Assert.Equal("", s.SkinDirectory);
    }

    [Fact]
    public void SetThenNewInstanceReadsSameValues()
    {
        var file = Path.Combine(_dir, "settings.json");
        var first = new JsonSettingsService(file);
        first.PetScale = 1.5;
        first.RoamInterval = 6.0;
        first.StatDecaySpeed = 0.5;
        first.SoundVolume = 0.3;
        first.MusicDance = true;
        first.SkinDirectory = @"C:\skins\kitty";

        Assert.True(File.Exists(file));

        var second = new JsonSettingsService(file);
        Assert.Equal(1.5, second.PetScale);
        Assert.Equal(6.0, second.RoamInterval);
        Assert.Equal(0.5, second.StatDecaySpeed);
        Assert.Equal(0.3, second.SoundVolume);
        Assert.True(second.MusicDance);
        Assert.Equal(@"C:\skins\kitty", second.SkinDirectory);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsWithoutThrowing()
    {
        var file = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "{ this is not valid json !!!");

        var s = new JsonSettingsService(file);

        Assert.Equal(1.0, s.PetScale);
        Assert.Equal(4.0, s.RoamInterval);
        Assert.False(s.MusicDance);
        Assert.Equal("", s.SkinDirectory);
    }

    [Fact]
    public void PartialFile_UsesPresentValuesAndDefaultsForMissingKeys()
    {
        var file = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "{\"PetScale\": 2.0, \"MusicDance\": true}");

        var s = new JsonSettingsService(file);

        Assert.Equal(2.0, s.PetScale);
        Assert.True(s.MusicDance);
        Assert.Equal(4.0, s.RoamInterval);
        Assert.Equal("", s.SkinDirectory);
    }

    [Fact]
    public void EmptyFile_FallsBackToDefaultsWithoutThrowing()
    {
        var file = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "");

        var s = new JsonSettingsService(file);

        Assert.Equal(1.0, s.PetScale);
        Assert.Equal(1.0, s.SoundVolume);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
