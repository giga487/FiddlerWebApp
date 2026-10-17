using Fiddler.Formats;
using Fiddler.Graphics.Gumps;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class GumpCompositionTests
{
    private static DecodedImage Image(int width = 20, int height = 20) => DecodedImage.FromArgb(width, height,
        Enumerable.Repeat(unchecked((int)0xFFFF0000), width * height).ToArray());

    [Fact]
    public void NonCellSizedPiecesPackWithoutStretchingAndRepeatedIdsRemainIndependent()
    {
        var scene = new GumpComposition();
        scene.Configure(128, 64, 16);
        var first = scene.Add(44, Image());
        var second = scene.Add(44, Image());
        Assert.Equal((0, 0), (first.X, first.Y));
        Assert.Equal((32, 0), (second.X, second.Y));
        Assert.Equal(20, second.Image.Width);
        Assert.NotEqual(first.Key, second.Key);
        scene.Move(second.Key, 19, 17);
        Assert.Equal((16, 16), (scene.Pieces[1].X, scene.Pieces[1].Y));
        Assert.Equal(second.Key, scene.HitTest(18, 18)?.Key);
        scene.AutoArrange();
        Assert.Equal((32, 0), (scene.Pieces[1].X, scene.Pieces[1].Y));
    }

    [Fact]
    public void MoveClampsToGridInsideBoardIncludingAtZoomIndependentPixelCoordinates()
    {
        var scene = new GumpComposition();
        scene.Configure(100, 100, 16);
        var piece = scene.Add(44, Image());
        scene.Move(piece.Key, 999, -20);
        Assert.Equal((80, 0), (scene.Pieces[0].X, scene.Pieces[0].Y));
        Assert.Null(scene.HitTest(100, 0));
        Assert.Throws<ArgumentException>(() => scene.Move(piece.Key, double.NaN, 0));
    }

    [Fact]
    public void FailedResizeAndAddPreserveExistingComposition()
    {
        var scene = new GumpComposition();
        scene.Add(44, Image(80, 80));
        Assert.Throws<InvalidOperationException>(() => scene.Configure(64, 64, 16));
        Assert.Equal(768, scene.Width);
        Assert.Equal(32, scene.CellSize);
        Assert.Single(scene.Pieces);
        Assert.Throws<InvalidOperationException>(() => scene.Add(45, Image(800, 10)));
        Assert.Single(scene.Pieces);
    }

    [Fact]
    public void ReplacementKeepsInstanceAndFitsToGridAndRemovalReleasesSpace()
    {
        var scene = new GumpComposition();
        scene.Configure(128, 64, 16);
        var piece = scene.Add(44, Image());
        scene.Move(piece.Key, 100, 0);
        scene.ReplaceImage(piece.Key, Image(60, 32));
        Assert.Equal(64, scene.Pieces[0].X);
        Assert.Equal(44, scene.Pieces[0].GumpId);
        scene.Remove(piece.Key);
        Assert.Empty(scene.Pieces);
        Assert.Equal(0, scene.Add(45, Image()).X);
    }

    [Fact]
    public void SkiaDrawsPiecesAtOriginalSizeAndOptionalGridAndSelection()
    {
        var scene = new GumpComposition();
        scene.Configure(64, 64, 16);
        var piece = scene.Add(44, Image());
        scene.Move(piece.Key, 16, 16);
        var renderer = new GumpCompositionRenderer();
        using var plain = SKBitmap.Decode(renderer.RenderPng(scene, showGrid: false));
        Assert.Equal(64, plain.Width);
        Assert.Equal(SKColors.Red, plain.GetPixel(17, 17));
        Assert.NotEqual(SKColors.Red, plain.GetPixel(36, 17));
        using var grid = SKBitmap.Decode(renderer.RenderPng(scene, piece.Key));
        Assert.NotEqual(plain.GetPixel(16, 16), grid.GetPixel(16, 16));
    }
}
