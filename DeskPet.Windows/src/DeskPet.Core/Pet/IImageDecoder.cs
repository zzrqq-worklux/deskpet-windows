namespace DeskPet.Core.Pet;

public sealed record DecodedFrame(int Width, int Height, byte[] Rgba, int? BufferWidth = null, int? BufferHeight = null)
{
    public int SigWidth => BufferWidth ?? Width;
    public int SigHeight => BufferHeight ?? Height;
}

public interface IImageDecoder
{
    /// <summary>Decode the image at <paramref name="path"/> downsampled so its longest edge is at most
    /// <paramref name="maxPixel"/> pixels. Width/Height are always the source image's original pixel
    /// dimensions (the Rgba buffer may be smaller; BufferWidth/BufferHeight report the actual buffer
    /// dimensions when downsample occurred). Returns null when the image cannot be decoded.</summary>
    Task<DecodedFrame?> DecodeAsync(string path, int maxPixel);
}
