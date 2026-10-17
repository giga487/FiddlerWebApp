using Fiddler.Formats;
using SkiaSharp;

namespace Fiddler.Graphics.World;

public sealed record WorldTile(int Key, int Id, int X, int Y, int Z, bool Land, int Hue = 0, int Flags = 1);
public sealed record SceneHit(WorldTile Tile, int X, int Y, DecodedImage Image);
public sealed record ScenePreview(byte[] Png, int Width, int Height, IReadOnlyList<SceneHit> Hits, IReadOnlyList<string> Warnings)
{
    public WorldTile? HitTest(double x, double y)
    {
        foreach (var hit in Hits.Reverse())
        {
            int localX = (int)Math.Floor(x - hit.X), localY = (int)Math.Floor(y - hit.Y);
            if (localX >= 0 && localY >= 0 && localX < hit.Image.Width && localY < hit.Image.Height &&
                (uint)hit.Image.Pixels.Span[localY * hit.Image.Width + localX] >> 24 != 0) return hit.Tile;
        }
        return null;
    }
}

/// <summary>Bounded isometric asset preview: 22 px per tile axis, 4 px per Z, no UI dependencies.</summary>
public sealed class IsometricSceneRenderer
{
    private readonly SkiaAssetRenderer assets = new();
    public ScenePreview Render(IEnumerable<WorldTile> source, Func<int, bool, DecodedImage> readArt, int? selectedKey = null)
    {
        var tiles = source.Take(10_001).ToArray();
        if (tiles.Length > 10_000) throw new InvalidDataException("Preview is limited to 10,000 pieces: reduce the area or maximum Z.");
        var cache = new Dictionary<(int, bool), DecodedImage>();
        var warnings = new List<string>();
        var drawn = new List<SceneHit>();
        long pixels = 0;
        foreach (var tile in tiles.OrderBy(t => t.X + t.Y).ThenBy(t => t.X).ThenBy(t => t.Z).ThenBy(t => t.Land ? 0 : 1).ThenBy(t => t.Key))
        {
            if (!cache.TryGetValue((tile.Id, tile.Land), out var image))
            {
                try { image = readArt(tile.Id, tile.Land); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException)
                {
                    if (warnings.Count < 32) warnings.Add($"{(tile.Land ? "Land" : "Item")} 0x{tile.Id:X4}: {ex.Message}");
                    image = MissingArt(tile.Land);
                }
                pixels += (long)image.Width * image.Height;
                if (pixels > 16_000_000) throw new InvalidDataException("Too many preview images: reduce the region.");
                cache[(tile.Id, tile.Land)] = image;
            }
            int x = (tile.X - tile.Y) * 22 - image.Width / 2;
            int y = (tile.X + tile.Y) * 22 - tile.Z * 4 - (tile.Land ? 22 : image.Height);
            drawn.Add(new(tile, x, y, image));
        }
        int left = drawn.Count == 0 ? 0 : drawn.Min(h => h.X), top = drawn.Count == 0 ? 0 : drawn.Min(h => h.Y);
        int width = drawn.Count == 0 ? 128 : drawn.Max(h => h.X + h.Image.Width) - left + 16;
        int height = drawn.Count == 0 ? 128 : drawn.Max(h => h.Y + h.Image.Height) - top + 16;
        if (width > 2048 || height > 2048 || (long)width * height > 4_000_000)
            throw new InvalidDataException("Preview exceeds 2048 px or 4 million pixels: reduce the area or maximum Z.");
        using var surface = SKSurface.Create(new SKImageInfo(width, height)) ?? throw new InvalidOperationException("Skia surface is not available.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(240, 244, 244));
        var hits = new List<SceneHit>(drawn.Count);
        foreach (var hit in drawn)
        {
            var positioned = hit with { X = hit.X - left + 8, Y = hit.Y - top + 8 };
            hits.Add(positioned);
            assets.Draw(canvas, hit.Image, new SKRect(positioned.X, positioned.Y, positioned.X + hit.Image.Width, positioned.Y + hit.Image.Height));
        }
        if (hits.FirstOrDefault(h => h.Tile.Key == selectedKey) is { } selected)
        {
            using var pen = new SKPaint { Color = new SKColor(0, 150, 130), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(selected.X, selected.Y, selected.Image.Width, selected.Image.Height, pen);
        }
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return new(png.ToArray(), width, height, hits.AsReadOnly(), warnings.AsReadOnly());
    }
    private static DecodedImage MissingArt(bool land)
    {
        int size = land ? 44 : 16;
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            if (!land || Math.Abs(x - 21.5) + Math.Abs(y - 21.5) <= 22)
                pixels[y * size + x] = ((x / 4 + y / 4) % 2 == 0) ? unchecked((int)0xFFFF00FF) : unchecked((int)0xFF550055);
        return DecodedImage.FromArgb(size, size, pixels);
    }
}
