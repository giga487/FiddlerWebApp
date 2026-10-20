using Fiddler.Formats.Hues;

namespace Fiddler.Formats.Art;

public static class ArtEncoder
{
    private static ushort Color(int pixel) => HuePalette.FromRgb((pixel >> 16) & 255, (pixel >> 8) & 255, pixel & 255);
    public static byte[] Land(DecodedImage image)
    {
        if (image.Width != 44 || image.Height != 44) throw new ArgumentException("Land art must be exactly 44 × 44 pixels. Only the diamond is stored.");
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        for (int y = 0; y < 44; y++)
        {
            int count = y < 22 ? (y + 1) * 2 : (44 - y) * 2, start = (44 - count) / 2;
            for (int x = 0; x < count; x++) writer.Write(Color(image.Pixels.Span[y * 44 + start + x]));
        }
        return buffer.ToArray();
    }
    public static byte[] Item(DecodedImage image)
    {
        using var runs = new MemoryStream(); using var writer = new BinaryWriter(runs);
        var lookup = new ushort[image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            if (runs.Position / 2 > ushort.MaxValue) throw new InvalidDataException("Item data exceeds the 16-bit row-offset limit. Use a smaller image.");
            lookup[y] = (ushort)(runs.Position / 2);
            int x = 0;
            while (x < image.Width)
            {
                int start = x;
                while (x < image.Width && (uint)image.Pixels.Span[y * image.Width + x] >> 24 < 128) x++;
                int skip = x - start, first = x;
                while (x < image.Width && (uint)image.Pixels.Span[y * image.Width + x] >> 24 >= 128) x++;
                int count = x - first;
                if (count == 0) break;
                writer.Write((ushort)skip); writer.Write((ushort)count);
                for (int i = first; i < x; i++) writer.Write(Color(image.Pixels.Span[y * image.Width + i]));
            }
            writer.Write(0u);
        }
        using var buffer = new MemoryStream(); using var output = new BinaryWriter(buffer);
        output.Write(0); output.Write((ushort)image.Width); output.Write((ushort)image.Height);
        foreach (ushort offset in lookup) output.Write(offset);
        output.Write(runs.ToArray()); return buffer.ToArray();
    }
}
