using System;
using System.Collections.Generic;
using System.Reflection;
using DeskPet.Core.Services;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Services;

public class ServiceContractsTests
{
    [Fact]
    public void AllFourInterfacesAreInterfaces()
    {
        Assert.True(typeof(ISettingsService).IsInterface);
        Assert.True(typeof(IAudioService).IsInterface);
        Assert.True(typeof(IMusicService).IsInterface);
        Assert.True(typeof(IScreenInfo).IsInterface);
    }

    [Fact]
    public void RectIsARecordWithValueEquality()
    {
        var r1 = new Rect(10, 20, 30, 40);
        var r2 = new Rect(10, 20, 30, 40);
        var r3 = new Rect(10, 20, 30, 41);

        Assert.True(r1 == r2);
        Assert.True(r1.Equals(r2));
        Assert.Equal(r1.GetHashCode(), r2.GetHashCode());
        Assert.False(r1 == r3);
    }

    [Fact]
    public void RectExposesPositionalMembers()
    {
        var r = new Rect(1.5, 2.5, 3.5, 4.5);

        Assert.Equal(1.5, r.X);
        Assert.Equal(2.5, r.Y);
        Assert.Equal(3.5, r.Width);
        Assert.Equal(4.5, r.Height);
    }

    [Fact]
    public void ISettingsServiceExposesReadWriteDoubleAndBoolAndStringProperties()
    {
        var t = typeof(ISettingsService);

        Assert.Equal(typeof(double), t.GetProperty(nameof(ISettingsService.PetScale))?.PropertyType);
        Assert.Equal(typeof(double), t.GetProperty(nameof(ISettingsService.RoamInterval))?.PropertyType);
        Assert.Equal(typeof(double), t.GetProperty(nameof(ISettingsService.StatDecaySpeed))?.PropertyType);
        Assert.Equal(typeof(double), t.GetProperty(nameof(ISettingsService.SoundVolume))?.PropertyType);
        Assert.Equal(typeof(bool), t.GetProperty(nameof(ISettingsService.MusicDance))?.PropertyType);
        Assert.Equal(typeof(string), t.GetProperty(nameof(ISettingsService.SkinDirectory))?.PropertyType);
    }

    [Fact]
    public void ISettingsServicePropertiesAreAllReadWrite()
    {
        var t = typeof(ISettingsService);
        foreach (var name in new[]
        {
            nameof(ISettingsService.PetScale), nameof(ISettingsService.RoamInterval),
            nameof(ISettingsService.StatDecaySpeed), nameof(ISettingsService.SoundVolume),
            nameof(ISettingsService.MusicDance), nameof(ISettingsService.SkinDirectory)
        })
        {
            var p = t.GetProperty(name);
            Assert.NotNull(p);
            Assert.True(p!.CanRead && p.CanWrite, $"{name} must be readable and writable");
        }
    }

    [Fact]
    public void IAudioServiceExposesPlayAndStopMethods()
    {
        var t = typeof(IAudioService);

        var play = t.GetMethod(nameof(IAudioService.Play), new[] { typeof(string), typeof(double) });
        Assert.NotNull(play);
        Assert.Equal(typeof(void), play!.ReturnType);

        var stop = t.GetMethod(nameof(IAudioService.Stop), Type.EmptyTypes);
        Assert.NotNull(stop);
        Assert.Equal(typeof(void), stop!.ReturnType);
    }

    [Fact]
    public void IMusicServiceExposesReadOnlyIsPlaying()
    {
        var t = typeof(IMusicService);

        var p = t.GetProperty(nameof(IMusicService.IsPlaying));
        Assert.NotNull(p);
        Assert.Equal(typeof(bool), p!.PropertyType);
        Assert.True(p.CanRead);
        Assert.False(p.CanWrite);
    }

    [Fact]
    public void IScreenInfoExposesWorkAreasAndPrimaryWorkArea()
    {
        var t = typeof(IScreenInfo);

        var areas = t.GetProperty(nameof(IScreenInfo.WorkAreas));
        Assert.NotNull(areas);
        Assert.Equal(typeof(IReadOnlyList<Rect>), areas!.PropertyType);
        Assert.True(areas.CanRead);
        Assert.False(areas.CanWrite);

        var primary = t.GetProperty(nameof(IScreenInfo.PrimaryWorkArea));
        Assert.NotNull(primary);
        Assert.Equal(typeof(Rect), primary!.PropertyType);
        Assert.True(primary.CanRead);
        Assert.False(primary.CanWrite);

        var dpiScale = t.GetProperty(nameof(IScreenInfo.DpiScale));
        Assert.NotNull(dpiScale);
        Assert.Equal(typeof(double), dpiScale!.PropertyType);
        Assert.True(dpiScale.CanRead);
        Assert.False(dpiScale.CanWrite);
    }

    [Fact]
    public void ServiceTypesLiveInDeskPetCoreServicesNamespace()
    {
        var ns = "DeskPet.Core.Services";

        Assert.Equal(ns, typeof(ISettingsService).Namespace);
        Assert.Equal(ns, typeof(IAudioService).Namespace);
        Assert.Equal(ns, typeof(IMusicService).Namespace);
        Assert.Equal(ns, typeof(IScreenInfo).Namespace);
        Assert.Equal(ns, typeof(Rect).Namespace);
    }
}
