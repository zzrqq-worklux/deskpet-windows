using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskPet.Core.Pet;

namespace DeskPet.Windows.Services;

/// <summary>WPF-based <see cref="IImageDecoder"/>. Decodes PNG/JPG on a background thread,
/// produces RGBA byte[] downsampled to <paramref name="maxPixel"/> on its longest edge while
/// keeping the source image's original pixel dimensions in <see cref="DecodedFrame"/>.</summary>
public sealed class WpfImageDecoder : IImageDecoder
{
    public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel)
        => Task.Run(() => Decode(path, maxPixel));

    /// <summary>Exact pixel dimensions of the RGBA buffer produced for the given source size,
    /// deterministic so the consumer can rebuild a BitmapSource without extra state.</summary>
    public static (int Width, int Height) BufferDims(int origWidth, int origHeight, int maxPixel)
    {
        var maxDim = Math.Max(origWidth, origHeight);
        if (maxPixel <= 0 || maxDim <= maxPixel) return (origWidth, origHeight);
        var ratio = maxPixel / (double)maxDim;
        return (Math.Max(1, (int)Math.Round(origWidth * ratio)), Math.Max(1, (int)Math.Round(origHeight * ratio)));
    }

    /// <summary>Builds a frozen <see cref="BitmapSource"/> from a decoded frame (RGBA -&gt; BGRA swap).</summary>
    public static BitmapSource ToBitmapSource(DecodedFrame frame, int maxPixel)
    {
        var (w, h) = BufferDims(frame.Width, frame.Height, maxPixel);
        var bgra = new byte[frame.Rgba.Length];
        for (var i = 0; i < frame.Rgba.Length; i += 4)
        {
            bgra[i] = frame.Rgba[i + 2];
            bgra[i + 1] = frame.Rgba[i + 1];
            bgra[i + 2] = frame.Rgba[i];
            bgra[i + 3] = frame.Rgba[i + 3];
        }
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        source.Freeze();
        return source;
    }

    private DecodedFrame? Decode(string path, int maxPixel)
    {
        try
        {
            byte[] bgra;
            int srcW, srcH;
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame == null) return null;
                srcW = frame.PixelWidth;
                srcH = frame.PixelHeight;
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                bgra = new byte[srcW * srcH * 4];
                converted.CopyPixels(bgra, srcW * 4, 0);
            }
            var (bufW, bufH) = BufferDims(srcW, srcH, maxPixel);
            var rgba = bufW == srcW && bufH == srcH
                ? BgraToRgba(bgra)
                : BoxDownsample(bgra, srcW, srcH, bufW, bufH);
            return new DecodedFrame(srcW, srcH, rgba, bufW, bufH);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }

    private static byte[] BoxDownsample(byte[] bgra, int srcW, int srcH, int dstW, int dstH)
    {
        var outBytes = new byte[dstW * dstH * 4];
        for (var dy = 0; dy < dstH; dy++)
        {
            var sy0 = dy * srcH / dstH;
            var sy1 = Math.Max(sy0 + 1, (dy + 1) * srcH / dstH);
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx0 = dx * srcW / dstW;
                var sx1 = Math.Max(sx0 + 1, (dx + 1) * srcW / dstW);
                int b = 0, g = 0, r = 0, a = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var i = (sy * srcW + sx) * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        a += bgra[i + 3];
                    }
                }
                var n = (sy1 - sy0) * (sx1 - sx0);
                var o = (dy * dstW + dx) * 4;
                outBytes[o] = (byte)(r / n);
                outBytes[o + 1] = (byte)(g / n);
                outBytes[o + 2] = (byte)(b / n);
                outBytes[o + 3] = (byte)(a / n);
            }
        }
        return outBytes;
    }
}