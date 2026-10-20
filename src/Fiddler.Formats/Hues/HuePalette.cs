using Fiddler.Formats.Imaging;

namespace Fiddler.Formats.Hues;

public enum HueMode { Full, GrayPixelsOnly }

/// <summary>A UO hue is a 32-entry RGB555 lookup table, rather than a flat tint.</summary>
public static class HuePalette
{
    public static ushort FromRgb(int r, int g, int b)
    {
        if ((uint)r > 255 || (uint)g > 255 || (uint)b > 255)
            throw new ArgumentOutOfRangeException(nameof(r));
        return (ushort)(((r * 31 + 127) / 255 << 10) | ((g * 31 + 127) / 255 << 5) | (b * 31 + 127) / 255);
    }

    public static int ToArgb(ushort color) => PixelDecoding.Color(color);

    public static ushort[] Gradient(ushort shadow, ushort middle, ushort highlight)
    {
        var colors = new ushort[32];
        for (int i = 0; i < colors.Length; i++)
        {
            ushort a = i <= 15 ? shadow : middle, b = i <= 15 ? middle : highlight;
            int step = i <= 15 ? i : i - 15, length = i <= 15 ? 15 : 16;
            int Blend(int shift) => ((((a >> shift) & 31) * (length - step) + ((b >> shift) & 31) * step + length / 2) / length) << shift;
            colors[i] = (ushort)(Blend(10) | Blend(5) | Blend(0));
        }
        return colors;
    }

    public static DecodedImage Apply(DecodedImage image, ReadOnlySpan<ushort> colors, HueMode mode)
    {
        if (colors.Length != 32) throw new ArgumentException("A hue must contain exactly 32 colors.", nameof(colors));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        int[] pixels = image.Pixels.ToArray();
        for (int i = 0; i < pixels.Length; i++)
        {
            int pixel = pixels[i];
            if ((uint)pixel >> 24 == 0) continue;
            int r = (pixel >> 19) & 31, g = (pixel >> 11) & 31, b = (pixel >> 3) & 31;
            if (mode == HueMode.GrayPixelsOnly && (r != g || r != b)) continue;
            pixels[i] = (pixel & unchecked((int)0xFF000000)) | (ToArgb(colors[r]) & 0xFFFFFF);
        }
        return DecodedImage.FromArgb(image.Width, image.Height, pixels);
    }
}
