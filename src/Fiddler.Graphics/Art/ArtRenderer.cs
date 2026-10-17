using Fiddler.Formats.Art;
using SkiaSharp;

namespace Fiddler.Graphics.Art;

/// <summary>Art-specific entry points for consumers that work with MUL records instead of decoded pixels.</summary>
public sealed class ArtRenderer
{
    private readonly SkiaAssetRenderer images = new();

    public byte[] RenderLandPng(ReadOnlySpan<byte> record, int scale = 1) =>
        images.RenderPng(LandDecoder.Decode(record), scale);

    public byte[] RenderItemPng(ReadOnlySpan<byte> record, int scale = 1) =>
        images.RenderPng(StaticArtDecoder.Decode(record), scale);

    public void DrawLand(SKCanvas canvas, ReadOnlySpan<byte> record, SKRect destination) =>
        images.Draw(canvas, LandDecoder.Decode(record), destination);

    public void DrawItem(SKCanvas canvas, ReadOnlySpan<byte> record, SKRect destination) =>
        images.Draw(canvas, StaticArtDecoder.Decode(record), destination);
}
