using System.Runtime.InteropServices;
using Fiddler.Formats;
using Fiddler.Formats.Gumps;
using SkiaSharp;

namespace Fiddler.Graphics.Gumps;

public static class GumpImageImporter
{
    public const int MaxUploadBytes = 8 * 1024 * 1024;

    public static GumpRecord ImportPng(byte[] data)
    {
        if (data.Length > MaxUploadBytes || data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            throw new InvalidDataException("Select a valid PNG, maximum 8 MiB.");
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Cannot read PNG.");
        int width = codec.Info.Width, height = codec.Info.Height;
        if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 4_000_000)
            throw new InvalidDataException("PNG exceeds the limits: 4096 px per side and 4 million pixels.");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("PNG is truncated or cannot be decoded.");
        var pixels = new int[width * height];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return GumpEncoder.Encode(DecodedImage.FromArgb(width, height, pixels));
    }
}
