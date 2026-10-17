using System.Runtime.InteropServices;
using Fiddler.Formats;
using SkiaSharp;

namespace Fiddler.Graphics;

/// <summary>Reusable Skia renderer: no Blazor, HTTP, Windows Forms or System.Drawing dependencies.</summary>
public sealed class SkiaAssetRenderer
{
    public SKBitmap CreateBitmap(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height,
            SKColorType.Bgra8888, SKAlphaType.Unpremul));
        try
        {
            Marshal.Copy(image.Pixels.ToArray(), 0, bitmap.GetPixels(), image.Pixels.Length);
            return bitmap; // Ownership is transferred to the caller.
        }
        catch { bitmap.Dispose(); throw; }
    }

    public void Draw(SKCanvas canvas, DecodedImage image, SKRect destination)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        using var bitmap = CreateBitmap(image);
        using var skImage = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(skImage, destination, new SKSamplingOptions(SKFilterMode.Nearest));
    }

    public byte[] RenderPng(DecodedImage image, int scale = 1)
    {
        if (scale < 1 || scale > 8 || (long)image.Width * image.Height * scale * scale > 16_000_000)
            throw new ArgumentOutOfRangeException(nameof(scale));
        return Render(image, image.Width * scale, image.Height * scale);
    }

    public byte[] RenderThumbnail(DecodedImage image, int maxWidth = 112, int maxHeight = 96)
    {
        if (maxWidth < 1 || maxHeight < 1 || maxWidth > 1024 || maxHeight > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxWidth));
        double ratio = Math.Min(1, Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height));
        return Render(image, Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio)));
    }

    private byte[] Render(DecodedImage image, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height))
            ?? throw new InvalidOperationException("Cannot create the Skia surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        Draw(surface.Canvas, image, new SKRect(0, 0, width, height));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
