using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class PetAnimationLoopTests
{
    private static PetAnimationLoop NewLoop(bool isLoop, int total, int cached, int loopPoint = 0, double fps = 1)
    {
        return new PetAnimationLoop
        {
            Fps = fps,
            IsLoop = isLoop,
            TotalFrames = total,
            CachedCount = cached,
            LoopPoint = loopPoint
        };
    }

    private static int[] AdvanceMany(PetAnimationLoop loop, int n, double tick = 1)
    {
        var idx = new int[n];
        for (var i = 0; i < n; i++)
        {
            loop.Advance(tick);
            idx[i] = loop.CurrentIndex ?? -1;
        }
        return idx;
    }

    [Fact]
    public void Loop_BasicModulo_WrapsByCachedCount()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 10);

        var idx = AdvanceMany(loop, 11);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 }, idx);
        Assert.False(loop.OneShotFinished);
    }

    [Fact]
    public void Loop_SeamlessLoopPoint_WrapsToLoopPointAfterFirstPass()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 10, loopPoint: 7);

        var idx = AdvanceMany(loop, 13);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 7, 8, 9 }, idx);
    }

    [Fact]
    public void Loop_PartialCache_ModuloByCachedCountNotTotal()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 5);

        var idx = AdvanceMany(loop, 10);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 0, 1, 2, 3, 4 }, idx);
    }

    [Fact]
    public void Loop_SeamlessNotUsedWhilePartialCache()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 5, loopPoint: 3);

        var idx = AdvanceMany(loop, 12);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 0, 1, 2, 3, 4, 0, 1 }, idx);
    }

    [Fact]
    public void OneShot_AdvancesToEnd_ThenFinishes()
    {
        var loop = NewLoop(isLoop: false, total: 10, cached: 10);

        var idx = AdvanceMany(loop, 10);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, idx);
        Assert.True(loop.OneShotFinished);
        Assert.Equal(9, loop.CurrentIndex);
    }

    [Fact]
    public void OneShot_FinishedUsesTotalFramesNotCachedCount()
    {
        var loop = NewLoop(isLoop: false, total: 10, cached: 4);

        var changed = true;
        for (var i = 0; i < 4; i++) changed = loop.Advance(1);

        Assert.Equal(3, loop.CurrentIndex);
        Assert.False(loop.OneShotFinished);
        Assert.True(changed);

        changed = loop.Advance(1);
        Assert.False(changed);
        Assert.False(loop.OneShotFinished);

        for (var i = 0; i < 5; i++) loop.Advance(1);

        Assert.True(loop.OneShotFinished);
        Assert.Equal(3, loop.CurrentIndex);
    }

    [Fact]
    public void OneShot_PartialCache_IndexClampedToCachedCount()
    {
        var loop = NewLoop(isLoop: false, total: 10, cached: 4);

        var idx = AdvanceMany(loop, 10);

        Assert.Equal(new[] { 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 }, idx);
        Assert.True(loop.OneShotFinished);
        Assert.Equal(3, loop.CurrentIndex);
    }

    [Fact]
    public void OneShot_TotalFramesZero_NeverFinishes()
    {
        var loop = NewLoop(isLoop: false, total: 0, cached: 5);

        var idx = AdvanceMany(loop, 20);

        Assert.Equal(4, idx[19]);
        Assert.False(loop.OneShotFinished);
        Assert.Equal(4, loop.CurrentIndex);
    }

    [Fact]
    public void Advance_NoFrameChange_ReturnsFalse_NoRedraw()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 10);

        Assert.True(loop.Advance(0.5));
        Assert.Equal(0, loop.CurrentIndex);
        Assert.False(loop.Advance(0.5));
        Assert.Equal(0, loop.CurrentIndex);
        Assert.True(loop.Advance(0.5));
        Assert.Equal(1, loop.CurrentIndex);
        Assert.False(loop.Advance(0.5));
        Assert.Equal(1, loop.CurrentIndex);
        Assert.True(loop.Advance(0.5));
        Assert.Equal(2, loop.CurrentIndex);
        Assert.False(loop.Advance(0.5));
        Assert.Equal(2, loop.CurrentIndex);
    }

    [Fact]
    public void CachedCountZero_NoAdvance_NullIndex()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 0);

        Assert.False(loop.Advance(1));
        Assert.Null(loop.CurrentIndex);

        loop.CachedCount = 5;

        Assert.True(loop.Advance(1));
        Assert.Equal(0, loop.CurrentIndex);
    }

    [Fact]
    public void CachedCountZero_OneShot_NeverStarts()
    {
        var loop = NewLoop(isLoop: false, total: 10, cached: 0);

        Assert.False(loop.Advance(1));
        Assert.Null(loop.CurrentIndex);
        Assert.False(loop.OneShotFinished);
    }

    [Fact]
    public void Reset_RestartsFromBeginning()
    {
        var loop = NewLoop(isLoop: false, total: 10, cached: 10);
        AdvanceMany(loop, 10);
        Assert.Equal(9, loop.CurrentIndex);
        Assert.True(loop.OneShotFinished);

        loop.Reset();

        Assert.Null(loop.CurrentIndex);
        Assert.False(loop.OneShotFinished);

        var idx = AdvanceMany(loop, 3);
        Assert.Equal(new[] { 0, 1, 2 }, idx);
    }

    [Fact]
    public void Reset_MidLoop_RestartsIndexSequence()
    {
        var loop = NewLoop(isLoop: true, total: 10, cached: 10);
        AdvanceMany(loop, 12);
        Assert.Equal(1, loop.CurrentIndex);

        loop.Reset();

        var idx = AdvanceMany(loop, 3);
        Assert.Equal(new[] { 0, 1, 2 }, idx);
    }
}
