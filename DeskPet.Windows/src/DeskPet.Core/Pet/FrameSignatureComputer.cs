namespace DeskPet.Core.Pet;

public static class FrameSignatureComputer
{
    private const int Grid = 16;

    public static float[] Compute(byte[] rgba, int width, int height)
    {
        var sig = new float[Grid * Grid * 2];
        if (rgba.Length < width * height * 4) return sig;
        if (width <= 0 || height <= 0) return sig;
        for (var gy = 0; gy < Grid; gy++)
        {
            for (var gx = 0; gx < Grid; gx++)
            {
                float lum = 0, alpha = 0, cnt = 0;
                for (var sy = 0; sy < Math.Max(1, height / Grid); sy++)
                {
                    for (var sx = 0; sx < Math.Max(1, width / Grid); sx++)
                    {
                        var x = Math.Min(width - 1, gx * (width / Grid) + sx);
                        var y = Math.Min(height - 1, gy * (height / Grid) + sy);
                        var i = (y * width + x) * 4;
                        var a = rgba[i + 3] / 255f;
                        if (a > 0.05f)
                        {
                            lum += (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3f / 255f * a;
                            alpha += a;
                            cnt += 1;
                        }
                    }
                }
                var n = Math.Max(1, height / Grid) * Math.Max(1, width / Grid);
                sig[gy * Grid + gx] = cnt > 0 ? lum / n : 0;
                sig[Grid * Grid + gy * Grid + gx] = alpha / n;
            }
        }
        return sig;
    }

    public static float Distance(float[] a, float[] b)
    {
        if (a.Length != b.Length) return float.MaxValue;
        float d = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            d += diff * diff;
        }
        return d;
    }

    public static int ComputeLoopPoint(float[][] signatures)
    {
        if (signatures.Length <= 3) return 0;
        var last = signatures.Length - 1;
        var limit = Math.Max(1, (int)(signatures.Length * 0.7));
        var best = 0;
        var bestDist = float.MaxValue;
        for (var s = 0; s < limit; s++)
        {
            var d = Distance(signatures[s], signatures[last]);
            if (d < bestDist)
            {
                bestDist = d;
                best = s;
            }
        }
        var defaultDist = Distance(signatures[0], signatures[last]);
        return bestDist < defaultDist * 0.5f ? best : 0;
    }
}
