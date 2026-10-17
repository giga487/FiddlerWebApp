using static Fiddler.Formats.Imaging.PixelDecoding;

namespace Fiddler.Formats.Art;

public static class StaticArtDecoder
{
    public static DecodedImage Decode(ReadOnlySpan<byte> data)
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
                    throw new InvalidDataException("Art run exceeds the row width.");
                x += skip;
                Ensure(data, position, count * 2);
                for (int i = 0; i < count; i++, position += 2)
                    pixels[y * width + x++] = Color(Word(data, position));
            }
        }
        return new DecodedImage(width, height, pixels);
    }
}
