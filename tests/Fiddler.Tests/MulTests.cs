using System.Buffers.Binary;
using Fiddler.Formats;
using Fiddler.Graphics;
using FiddlerWebApp.Services;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class MulTests
{
    // Hand-built format fixtures. No copyrighted client assets are included.
    private static byte[] Words(params ushort[] words)
    {
        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), words[i]);
        return bytes;
    }
    private static byte[] Land() => Words(Enumerable.Repeat((ushort)0x7C00, 1012).ToArray());
    // 4-byte header, width=3, height=2, row offsets (in words), then RLE rows.
    private static byte[] Art() => Words(0, 0, 3, 2, 0, 5,
        1, 1, 0x03E0, 0, 0, 0, 3, 0x001F, 0x7C00, 0, 0, 0);
    // Two 32-bit row offsets (in DWORDs), then (color, run length) pairs.
    private static byte[] Gump() => Words(2, 0, 4, 0, 0x7C00, 1, 0, 2, 0x03E0, 3);

    [Fact]
    public void LandHasDiamondShapeAndOpaqueBlack()
    {
        var decoded = MulImageDecoder.DecodeLand(Land());
        Assert.Equal((44, 44), (decoded.Width, decoded.Height));
        var pixels = decoded.Pixels.ToArray();
        Assert.Equal(1012, pixels.Count(p => p != 0));
        Assert.Equal(0, pixels[0]);
        Assert.Equal(unchecked((int)0xFFFF0000), pixels[21]);
        Assert.Equal(0, pixels[43]);
        Assert.Equal(unchecked((int)0xFFFF0000), pixels[43 * 44 + 21]);
        Assert.Equal(unchecked((int)0xFF000000), MulImageDecoder.DecodeLand(new byte[2024]).Pixels.Span[21]);
    }

    [Fact]
    public void ArtUsesRelativeRowOffsetsAndTransparentSkippedPixels()
    {
        var image = MulImageDecoder.DecodeStatic(Art());
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(new[] { 0, unchecked((int)0xFF00FF00), 0,
            unchecked((int)0xFF0000FF), unchecked((int)0xFFFF0000), unchecked((int)0xFF000000) }, image.Pixels.ToArray());
    }

    [Fact]
    public void GumpUsesDwordRowOffsetsAndZeroIsTransparent()
    {
        var image = MulImageDecoder.DecodeGump(Gump(), 3, 2);
        Assert.Equal(new[] { unchecked((int)0xFFFF0000), 0, 0,
            unchecked((int)0xFF00FF00), unchecked((int)0xFF00FF00), unchecked((int)0xFF00FF00) }, image.Pixels.ToArray());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(4097, 1)] [InlineData(4096, 4096)]
    public void OversizedOrEmptyDimensionsAreRejected(int width, int height) =>
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeGump([], width, height));

    [Fact]
    public void CorruptRunsOffsetsAndTruncatedRecordsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeLand(new byte[2023]));
        byte[] art = Art();
        BinaryPrimitives.WriteUInt16LittleEndian(art.AsSpan(14), 3); // skip=1, run=3, width=3
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeStatic(art));
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeStatic(Art()[..^1]));
        art = Art();
        BinaryPrimitives.WriteUInt16LittleEndian(art.AsSpan(8), ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeStatic(art));
        byte[] gump = Gump();
        BinaryPrimitives.WriteUInt32LittleEndian(gump, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeGump(gump, 3, 2));
        gump = Gump();
        BinaryPrimitives.WriteUInt16LittleEndian(gump.AsSpan(10), 0);
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeGump(gump, 3, 2));
        gump = Gump();
        BinaryPrimitives.WriteUInt16LittleEndian(gump.AsSpan(10), 4);
        Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeGump(gump, 3, 2));
    }

    [Fact]
    public void EveryTruncatedArtPrefixFailsWithFormatError()
    {
        var art = Art();
        for (int length = 0; length < art.Length; length++)
        {
            byte[] truncated = art[..length];
            Assert.Throws<InvalidDataException>(() => MulImageDecoder.DecodeStatic(truncated));
        }
    }

    [Fact]
    public void SkiaPngRoundTripPreservesColorsAlphaAndNearestNeighborScaling()
    {
        var renderer = new SkiaAssetRenderer();
        using var png = SKBitmap.Decode(renderer.RenderPng(MulImageDecoder.DecodeStatic(Art()), 2));
        Assert.Equal((6, 4), (png.Width, png.Height));
        Assert.Equal((byte)0, png.GetPixel(0, 0).Alpha);
        Assert.Equal(SKColors.Lime, png.GetPixel(2, 0));
        Assert.Equal(SKColors.Lime, png.GetPixel(3, 1));
        Assert.Equal(SKColors.Blue, png.GetPixel(0, 2));
        Assert.Equal(SKColors.Black, png.GetPixel(5, 3));
        using var thumb = SKBitmap.Decode(renderer.RenderThumbnail(MulImageDecoder.DecodeLand(Land()), 22, 22));
        Assert.Equal((22, 22), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void RendererCanDrawToAnExistingCanvasWithoutWebDependencies()
    {
        var renderer = new SkiaAssetRenderer();
        using var bitmap = new SKBitmap(3, 2);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
        renderer.Draw(canvas, MulImageDecoder.DecodeStatic(Art()), new SKRect(0, 0, 3, 2));
        Assert.Equal(SKColors.Magenta, bitmap.GetPixel(0, 0));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(1, 0));
    }

    [Fact]
    public void ArchiveSeparatesLandAndItemIdsAndRejectsOutOfBoundsEntries()
    {
        using var fixture = new TempDirectory();
        byte[] index = Enumerable.Repeat((byte)0xFF, (0x4001) * 12).ToArray();
        Entry(index, 0, 0, 2024, 0);
        Entry(index, 0x4000, 2024, Art().Length, 0);
        File.WriteAllBytes(fixture.PathFor("artidx.mul"), index);
        File.WriteAllBytes(fixture.PathFor("art.mul"), [..Land(), ..Art()]);
        using (var archive = MulArchive.Open(fixture.PathFor("artidx.mul"), fixture.PathFor("art.mul")))
        {
            Assert.Single(archive.GetEntries(AssetKind.Land));
            Assert.Equal(0, Assert.Single(archive.GetEntries(AssetKind.Item)).Id);
            Assert.Equal(44, archive.Read(AssetKind.Land, 0).Width);
            Assert.Equal(3, archive.Read(AssetKind.Item, 0).Width);
            Assert.Throws<KeyNotFoundException>(() => archive.Read(AssetKind.Item, -1));
            Assert.Throws<KeyNotFoundException>(() => archive.Read(AssetKind.Item, int.MaxValue));
            Assert.Throws<KeyNotFoundException>(() => archive.Read(AssetKind.Gump, 0));
        }
        Entry(index, 0x4000, int.MaxValue, 20, 0);
        File.WriteAllBytes(fixture.PathFor("artidx.mul"), index);
        using var invalid = MulArchive.Open(fixture.PathFor("artidx.mul"), fixture.PathFor("art.mul"));
        Assert.Throws<InvalidDataException>(() => invalid.Read(AssetKind.Item, 0));
    }

    [Fact]
    public void GumpArchiveReadsPackedDimensionsAndWorkspacesAreIndependent()
    {
        using var fixture = new TempDirectory();
        var index = new byte[12];
        Entry(index, 0, 0, Gump().Length, (3 << 16) | 2);
        File.WriteAllBytes(fixture.PathFor("gumpidx.mul"), index);
        File.WriteAllBytes(fixture.PathFor("gumpart.mul"), Gump());
        byte[] original = File.ReadAllBytes(fixture.PathFor("gumpart.mul"));
        using var first = new MulWorkspace();
        using var second = new MulWorkspace();
        first.Open(fixture.Root); second.Open(fixture.Root);
        first.Dispose();
        Assert.Equal(3, second.Read(Assert.Single(second.Entries(AssetKind.Gump))).Width);
        Assert.Throws<DirectoryNotFoundException>(() => second.Open(fixture.PathFor("missing")));
        Assert.True(second.IsOpen); // Failed open preserves the current workspace.
        Assert.Equal(original, File.ReadAllBytes(fixture.PathFor("gumpart.mul")));
        second.Dispose();
        using var exclusive = File.Open(fixture.PathFor("gumpart.mul"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void InvalidIndexLengthIsRejected()
    {
        using var fixture = new TempDirectory();
        File.WriteAllBytes(fixture.PathFor("bad.idx"), new byte[13]);
        Assert.Throws<InvalidDataException>(() => MulArchive.Open(fixture.PathFor("bad.idx"), fixture.PathFor("missing.mul")));
    }

    private static void Entry(byte[] index, int slot, int offset, int length, int extra)
    {
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(slot * 12), offset);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(slot * 12 + 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(slot * 12 + 8), extra);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("fiddler-tests-").FullName;
        public string PathFor(string name) => Path.Combine(Root, name);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
