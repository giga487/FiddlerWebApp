using static Fiddler.Formats.Imaging.PixelDecoding;

namespace Fiddler.Formats.Art;

public static class LandDecoder
{
    public static DecodedImage Decode(ReadOnlySpan<byte> data)
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
}
