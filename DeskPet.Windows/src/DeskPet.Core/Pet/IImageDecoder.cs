namespace DeskPet.Core.Pet;

public sealed record DecodedFrame(int Width, int Height, byte[] Rgba);

public interface IImageDecoder
{
    /// <summary>Decode the image at <paramref name="path"/> downsampled so its longest edge is at most
    /// <paramref name="maxPixel"/> pixels. Width/Height are always the source image's original pixel
    /// dimensions (the Rgba buffer may be smaller). Returns null when the image cannot be decoded.</summary>
    Task<DecodedFrame?> DecodeAsync(string path, int maxPixel);
}
