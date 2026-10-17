namespace Fiddler.Formats;

/// <summary>Unpremultiplied ARGB32 pixels, independent of any graphics or UI library.</summary>
public sealed class DecodedImage
{
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<int> Pixels { get; }

    public static DecodedImage FromArgb(int width, int height, ReadOnlySpan<int> pixels)
    {
        if (width <= 0 || height <= 0 || width > Imaging.PixelDecoding.MaxDimension || height > Imaging.PixelDecoding.MaxDimension ||
            (long)width * height > Imaging.PixelDecoding.MaxPixels || pixels.Length != (long)width * height)
            throw new ArgumentException("Dimensioni o numero di pixel non validi.");
        return new DecodedImage(width, height, pixels.ToArray());
    }

    internal DecodedImage(int width, int height, int[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}
