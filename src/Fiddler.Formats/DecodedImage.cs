namespace Fiddler.Formats;

/// <summary>Unpremultiplied ARGB32 pixels, independent of any graphics or UI library.</summary>
public sealed class DecodedImage
{
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<int> Pixels { get; }

    internal DecodedImage(int width, int height, int[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

public enum AssetKind { Land, Item, Gump }

public sealed record MulEntry(AssetKind Kind, int Id, int Offset, int Length, int Extra);
