using System.Buffers.Binary;

namespace Fiddler.Formats;

/// <summary>Strict, bounded readers for classic MUL land, static art and gump RLE.</summary>
public static class MulImageDecoder
{
    public const int MaxPixels = 4_000_000;
    public const int MaxDimension = 4096;

    public static DecodedImage DecodeLand(ReadOnlySpan<byte> data)
    {
        const int pixelCount = 1012;
        if (data.Length < pixelCount * 2)
            throw new InvalidDataException("Land tile troncata: servono 1012 pixel a 16 bit.");

        var pixels = new int[44 * 44];
        int position = 0;
        for (int y = 0; y < 44; y++)
        {
            int count = y < 22 ? (y + 1) * 2 : (44 - y) * 2;
            int start = (44 - count) / 2;
            for (int x = 0; x < count; x++, position += 2)
                pixels[y * 44 + start + x] = Color(Word(data, position));
        }
        return new DecodedImage(44, 44, pixels);
    }

    public static DecodedImage DecodeStatic(ReadOnlySpan<byte> data)
    {
        int width = Word(data, 4), height = Word(data, 6);
        int[] pixels = Allocate(width, height);
        int start = 8 + height * 2;
        Ensure(data, 0, start);
        for (int y = 0; y < height; y++)
        {
            int position = start + Word(data, 8 + y * 2) * 2;
            int x = 0;
            while (true)
            {
                int skip = Word(data, position), count = Word(data, position + 2);
                position += 4;
                if (skip == 0 && count == 0) break;
                if (skip > width - x || count > width - x - skip)
                    throw new InvalidDataException("Run art oltre la larghezza della riga.");
                x += skip;
                Ensure(data, position, count * 2);
                for (int i = 0; i < count; i++, position += 2)
                    pixels[y * width + x++] = Color(Word(data, position));
            }
        }
        return new DecodedImage(width, height, pixels);
    }

    public static DecodedImage DecodeGump(ReadOnlySpan<byte> data, int width, int height)
    {
        int[] pixels = Allocate(width, height);
        int tableLength = height * 4;
        Ensure(data, 0, tableLength);
        for (int y = 0; y < height; y++)
        {
            long rowOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(y * 4, 4)) * 4;
            if (rowOffset < tableLength || rowOffset > data.Length - 4)
                throw new InvalidDataException("Offset gump fuori dal record.");
            int position = (int)rowOffset, x = 0;
            while (x < width)
            {
                ushort color = Word(data, position);
                int count = Word(data, position + 2);
                position += 4;
                if (count == 0 || count > width - x)
                    throw new InvalidDataException("Run gump vuoto o oltre la larghezza della riga.");
                if (color != 0)
                    pixels.AsSpan(y * width + x, count).Fill(Color(color));
                x += count;
            }
        }
        return new DecodedImage(width, height, pixels);
    }

    private static int[] Allocate(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
            (long)width * height > MaxPixels)
            throw new InvalidDataException("Dimensioni immagine non valide o superiori ai limiti.");
        return new int[width * height];
    }

    private static ushort Word(ReadOnlySpan<byte> data, int position)
    {
        Ensure(data, position, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position, 2));
    }

    private static void Ensure(ReadOnlySpan<byte> data, int position, int count)
    {
        if (position < 0 || count < 0 || position > data.Length - count)
            throw new InvalidDataException("Record MUL troncato o offset non valido.");
    }

    private static int Color(ushort value)
    {
        static int Expand(int component) => (component << 3) | (component >> 2);
        return unchecked((int)0xFF000000) | Expand((value >> 10) & 31) << 16 |
            Expand((value >> 5) & 31) << 8 | Expand(value & 31);
    }
}
