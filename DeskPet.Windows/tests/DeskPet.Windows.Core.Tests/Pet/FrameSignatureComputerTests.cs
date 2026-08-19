using System;
using System.Linq;
using DeskPet.Core.Pet;
using Xunit;

namespace DeskPet.Windows.Core.Tests.Pet;

public class FrameSignatureComputerTests
{
    [Fact]
    public void Compute_Produces_512_Floats()
    {
        var rgba = new byte[8*8*4]; Array.Fill(rgba, (byte)255);
        var s = FrameSignatureComputer.Compute(rgba, 8, 8);
        Assert.Equal(512, s.Length);
    }
    [Fact]
    public void LoopPoint_Prefers_Last_Similar_Frame()
    {
        var sigs = Enumerable.Range(0, 13).Select(i => RandomFill(1234 + i)).ToArray();
        sigs[8] = sigs[12].ToArray();
        var lp = FrameSignatureComputer.ComputeLoopPoint(sigs);
        Assert.Equal(8, lp);
    }
    [Fact]
    public void Fewer_Than_4_Frames_Returns_Zero()
    {
        var sigs = new float[][] { RandomFill(1), RandomFill(2), RandomFill(3) };
        Assert.Equal(0, FrameSignatureComputer.ComputeLoopPoint(sigs));
    }
    private static float[] RandomFill(int seed) { var r = new Random(seed); return Enumerable.Range(0,512).Select(_ => (float)r.NextDouble()).ToArray(); }
}
