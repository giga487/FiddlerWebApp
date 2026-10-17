using Fiddler.Formats.Animations;
using SkiaSharp;

namespace Fiddler.Graphics.Animations;

public sealed record AnimationBounds(int Left, int Top, int Width, int Height);
public sealed record AnimationAtlas(byte[] Png, int FrameWidth, int FrameHeight, int Columns, int FrameCount);

/// <summary>All frames share the same UO anchor, including mirrored directions.</summary>
public sealed class AnimationRenderer
{
    private const int MaxAtlasPixels = 16_000_000;
    private readonly SkiaAssetRenderer images = new();

    public AnimationBounds GetBounds(AnimationClip clip)
    {
        var visible = clip.Frames.Where(frame => frame.Image is not null).ToArray();
        if (visible.Length == 0) throw new InvalidDataException("The sequence contains no visible frames.");
        int left = visible.Min(frame => -frame.CenterX);
        int top = visible.Min(frame => -frame.CenterY - frame.Image!.Height);
        int right = visible.Max(frame => -frame.CenterX + frame.Image!.Width);
        int bottom = visible.Max(frame => -frame.CenterY);
        return new AnimationBounds(left, top, right - left, bottom - top);
    }

    public void DrawFrame(SKCanvas canvas, AnimationClip clip, int frameIndex, float anchorX, float anchorY, float scale = 1)
    {
        if (frameIndex < 0 || frameIndex >= clip.Frames.Count) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (!float.IsFinite(scale) || scale <= 0 || scale > 8) throw new ArgumentOutOfRangeException(nameof(scale));
        var frame = clip.Frames[frameIndex];
        if (frame.Image is not { } image) return;
        float left = anchorX - frame.CenterX * scale;
        float top = anchorY - (frame.CenterY + image.Height) * scale;
        images.Draw(canvas, image, new SKRect(left, top, left + image.Width * scale, top + image.Height * scale));
    }

    public AnimationAtlas RenderAtlas(AnimationClip clip)
    {
        var bounds = GetBounds(clip);
        if (bounds.Width > 4096 || bounds.Height > 4096 ||
            (long)bounds.Width * bounds.Height * clip.Frames.Count > MaxAtlasPixels)
            throw new InvalidDataException("Animated preview exceeds the size limits.");
        int columns = Math.Min(clip.Frames.Count, Math.Max(1, 4096 / bounds.Width));
        int rows = (clip.Frames.Count + columns - 1) / columns;
        int width = columns * bounds.Width, height = rows * bounds.Height;
        if (height > 8192 || (long)width * height > MaxAtlasPixels)
            throw new InvalidDataException("Animation atlas exceeds the size limits.");
        using var surface = SKSurface.Create(new SKImageInfo(width, height))
            ?? throw new InvalidOperationException("Cannot create the Skia animation surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        for (int i = 0; i < clip.Frames.Count; i++)
            DrawFrame(surface.Canvas, clip, i, i % columns * bounds.Width - bounds.Left,
                i / columns * bounds.Height - bounds.Top);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new AnimationAtlas(png.ToArray(), bounds.Width, bounds.Height, columns, clip.Frames.Count);
    }
}
