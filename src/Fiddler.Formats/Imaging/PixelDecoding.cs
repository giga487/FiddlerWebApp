using System.Buffers.Binary;

namespace Fiddler.Formats.Imaging;

internal static class PixelDecoding
{
    internal const int MaxPixels = 4_000_000;
    internal const int MaxDimension = 4096;

    internal static int[] Allocate(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
            (long)width * height > MaxPixels)
            throw new InvalidDataException("Dimensioni immagine non valide o superiori ai limiti.");
        return new int[width * height];
    }

    internal static ushort Word(ReadOnlySpan<byte> data, int position)
    {
        Ensure(data, position, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position, 2));
    }

    internal static void Ensure(ReadOnlySpan<byte> data, int position, int count)
    {
        if (position < 0 || count < 0 || position > data.Length - count)
            throw new InvalidDataException("Record MUL troncato o offset non valido.");
    }

    internal static int Color(ushort value)
    {
        static int Expand(int component) => (component << 3) | (component >> 2);
        return unchecked((int)0xFF000000) | Expand((value >> 10) & 31) << 16 |
            Expand((value >> 5) & 31) << 8 | Expand(value & 31);
    }
}
