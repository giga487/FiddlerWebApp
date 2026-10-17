using System.Buffers.Binary;
using static Fiddler.Formats.Imaging.PixelDecoding;

namespace Fiddler.Formats.Animations;

/// <summary>Classic anim*.mul: 256-color palette, relative frame table, signed-coordinate RLE.</summary>
public static class AnimationDecoder
{
    public const int MaxFrames = 512;
    public const int MaxDecodedPixels = 16_000_000;

    public static AnimationClip Decode(ReadOnlySpan<byte> record, bool mirror = false)
    {
        Ensure(record, 0, 516);
        int count = BinaryPrimitives.ReadInt32LittleEndian(record[512..]);
        if (count < 1 || count > MaxFrames)
            throw new InvalidDataException("Frame count is invalid or exceeds the limits.");
        int tableEnd = 516 + count * 4;
        Ensure(record, 0, tableEnd);
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            long offset = 512L + BinaryPrimitives.ReadInt32LittleEndian(record.Slice(516 + i * 4, 4));
            if (offset < tableEnd || offset > record.Length - 8)
                throw new InvalidDataException("Frame offset extends beyond the animation record.");
            offsets[i] = (int)offset;
        }
        Span<int> palette = stackalloc int[256];
        for (int i = 0; i < palette.Length; i++)
        {
            ushort color = Word(record, i * 2);
            palette[i] = (color & 0x8000) == 0 ? Color(color) : 0;
        }
        var frames = new AnimationFrame[count];
        long decodedPixels = 0;
        for (int i = 0; i < count; i++)
        {
            int end = record.Length;
            foreach (int offset in offsets)
                if (offset > offsets[i]) end = Math.Min(end, offset);
            var frame = record.Slice(offsets[i], end - offsets[i]);
            Ensure(frame, 0, 8);
            int centerX = BinaryPrimitives.ReadInt16LittleEndian(frame);
            int centerY = BinaryPrimitives.ReadInt16LittleEndian(frame[2..]);
            int width = Word(frame, 4), height = Word(frame, 6);
            if (width == 0 || height == 0)
            {
                frames[i] = new AnimationFrame(null, centerX, centerY);
                continue; // Preserve timing for an intentionally empty frame.
            }
            decodedPixels += (long)width * height;
            if (decodedPixels > MaxDecodedPixels)
                throw new InvalidDataException("Animation exceeds the decoded pixel limit.");
            int[] pixels = Allocate(width, height);
            int position = 8;
            while (true)
            {
                Ensure(frame, position, 4);
                uint header = BinaryPrimitives.ReadUInt32LittleEndian(frame[position..]);
                position += 4;
                if (header == 0x7FFF7FFF) break;
                int x = Signed10((int)(header >> 22)) + centerX;
                int y = Signed10((int)(header >> 12)) + centerY + height;
                int length = (int)(header & 0xFFF);
                if (length == 0 || x < 0 || x >= width || y < 0 || y >= height || length > width - x)
                    throw new InvalidDataException("Animation run extends beyond the frame bounds.");
                Ensure(frame, position, length);
                for (int p = 0; p < length; p++)
                {
                    int targetX = mirror ? width - 1 - x - p : x + p;
                    pixels[y * width + targetX] = palette[frame[position++]];
                }
            }
            frames[i] = new AnimationFrame(new DecodedImage(width, height, pixels),
                mirror ? width - centerX : centerX, centerY);
        }
        return new AnimationClip(frames, mirror);
    }

    private static int Signed10(int value)
    {
        value &= 0x3FF;
        return (value & 0x200) == 0 ? value : value - 0x400;
    }
}
