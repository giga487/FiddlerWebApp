using Fiddler.Formats.Gumps;
using SkiaSharp;

namespace Fiddler.Graphics.Gumps;

/// <summary>Gump rendering keeps packed dimensions and RLE interpretation out of UI consumers.</summary>
public sealed class GumpRenderer
{
    private readonly SkiaAssetRenderer images = new();

    public byte[] RenderPng(ReadOnlySpan<byte> record, int width, int height, int scale = 1) =>
        images.RenderPng(GumpDecoder.Decode(record, width, height), scale);

    public void Draw(SKCanvas canvas, ReadOnlySpan<byte> record, int width, int height, SKRect destination) =>
        images.Draw(canvas, GumpDecoder.Decode(record, width, height), destination);
}
