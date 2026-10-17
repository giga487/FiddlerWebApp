using SkiaSharp;

namespace Fiddler.Graphics.Gumps;

/// <summary>Reusable composition renderer, independent of Blazor and browser input.</summary>
public sealed class GumpCompositionRenderer
{
    private readonly SkiaAssetRenderer assets = new();

    public byte[] RenderPng(GumpComposition composition, int? selectedKey = null, bool showGrid = true)
    {
        using var surface = SKSurface.Create(new SKImageInfo(composition.Width, composition.Height))
            ?? throw new InvalidOperationException("Cannot create the Skia board.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(240, 244, 244));
        foreach (var piece in composition.Pieces)
            assets.Draw(canvas, piece.Image, new SKRect(piece.X, piece.Y, piece.X + piece.Image.Width, piece.Y + piece.Image.Height));
        if (showGrid)
        {
            using var grid = new SKPaint { Color = new SKColor(55, 95, 95, 80), StrokeWidth = 1, IsAntialias = false };
            for (int x = 0; x < composition.Width; x += composition.CellSize) canvas.DrawLine(x + .5f, 0, x + .5f, composition.Height, grid);
            for (int y = 0; y < composition.Height; y += composition.CellSize) canvas.DrawLine(0, y + .5f, composition.Width, y + .5f, grid);
        }
        if (composition.Pieces.FirstOrDefault(p => p.Key == selectedKey) is { } selected)
        {
            using var outline = new SKPaint { Color = new SKColor(0, 120, 110), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(selected.X, selected.Y, selected.Image.Width, selected.Image.Height, outline);
        }
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
