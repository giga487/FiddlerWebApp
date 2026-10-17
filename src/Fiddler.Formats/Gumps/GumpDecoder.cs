using System.Buffers.Binary;
using static Fiddler.Formats.Imaging.PixelDecoding;

namespace Fiddler.Formats.Gumps;

public static class GumpDecoder
{
    public static DecodedImage Decode(ReadOnlySpan<byte> data, int width, int height)
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
}
