using System.Buffers.Binary;

namespace Fiddler.Formats.Gumps;

public static class GumpEncoder
{
    /// <summary>15-bit RGB, one-bit alpha; opaque quantized black becomes 0x0421 (near-black).</summary>
    public static GumpRecord Encode(DecodedImage image)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        var rows = new int[image.Height];
        writer.Write(new byte[image.Height * 4]);
        var pixels = image.Pixels.Span;
        for (int y = 0; y < image.Height; y++)
        {
            rows[y] = checked((int)memory.Position / 4);
            int x = 0;
            while (x < image.Width)
            {
                ushort color = Quantize(pixels[y * image.Width + x]);
                int run = 1;
                while (x + run < image.Width && Quantize(pixels[y * image.Width + x + run]) == color) run++;
                writer.Write(color); writer.Write((ushort)run);
                x += run;
            }
        }
        byte[] data = memory.ToArray();
        for (int y = 0; y < rows.Length; y++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(y * 4), rows[y]);
        return new GumpRecord(data, image.Width, image.Height);
    }

    private static ushort Quantize(int argb)
    {
        if ((uint)argb >> 24 < 128) return 0;
        int color = ((argb >> 19) & 31) << 10 | ((argb >> 11) & 31) << 5 | ((argb >> 3) & 31);
        return (ushort)(color == 0 ? 0x0421 : color);
    }
}
