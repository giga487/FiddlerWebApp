using System.Buffers.Binary;
using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;
using Fiddler.Formats.Containers.Mul;
using Fiddler.Graphics.Art;
using Fiddler.Graphics.Gumps;
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
        var decoded = LandDecoder.Decode(Land());
        Assert.Equal((44, 44), (decoded.Width, decoded.Height));
        var pixels = decoded.Pixels.ToArray();
        Assert.Equal(1012, pixels.Count(p => p != 0));
        Assert.Equal(0, pixels[0]);
        Assert.Equal(unchecked((int)0xFFFF0000), pixels[21]);
        Assert.Equal(0, pixels[43]);
        Assert.Equal(unchecked((int)0xFFFF0000), pixels[43 * 44 + 21]);
        Assert.Equal(unchecked((int)0xFF000000), LandDecoder.Decode(new byte[2024]).Pixels.Span[21]);
    }

    [Fact]
    public void ArtUsesRelativeRowOffsetsAndTransparentSkippedPixels()
    {
        var image = StaticArtDecoder.Decode(Art());
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(new[] { 0, unchecked((int)0xFF00FF00), 0,
            unchecked((int)0xFF0000FF), unchecked((int)0xFFFF0000), unchecked((int)0xFF000000) }, image.Pixels.ToArray());
    }

    [Fact]
    public void GumpUsesDwordRowOffsetsAndZeroIsTransparent()
    {
        var image = GumpDecoder.Decode(Gump(), 3, 2);
        Assert.Equal(new[] { unchecked((int)0xFFFF0000), 0, 0,
            unchecked((int)0xFF00FF00), unchecked((int)0xFF00FF00), unchecked((int)0xFF00FF00) }, image.Pixels.ToArray());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(4097, 1)] [InlineData(4096, 4096)]
    public void OversizedOrEmptyDimensionsAreRejected(int width, int height) =>
        Assert.Throws<InvalidDataException>(() => GumpDecoder.Decode([], width, height));

    [Fact]
    public void CorruptRunsOffsetsAndTruncatedRecordsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => LandDecoder.Decode(new byte[2023]));
        byte[] art = Art();
        BinaryPrimitives.WriteUInt16LittleEndian(art.AsSpan(14), 3); // skip=1, run=3, width=3
        Assert.Throws<InvalidDataException>(() => StaticArtDecoder.Decode(art));
        Assert.Throws<InvalidDataException>(() => StaticArtDecoder.Decode(Art()[..^1]));
        art = Art();
        BinaryPrimitives.WriteUInt16LittleEndian(art.AsSpan(8), ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => StaticArtDecoder.Decode(art));
        byte[] gump = Gump();
        BinaryPrimitives.WriteUInt32LittleEndian(gump, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => GumpDecoder.Decode(gump, 3, 2));
        gump = Gump();
        BinaryPrimitives.WriteUInt16LittleEndian(gump.AsSpan(10), 0);
        Assert.Throws<InvalidDataException>(() => GumpDecoder.Decode(gump, 3, 2));
        gump = Gump();
        BinaryPrimitives.WriteUInt16LittleEndian(gump.AsSpan(10), 4);
        Assert.Throws<InvalidDataException>(() => GumpDecoder.Decode(gump, 3, 2));
    }

    [Fact]
    public void EveryTruncatedArtPrefixFailsWithFormatError()
    {
        var art = Art();
        for (int length = 0; length < art.Length; length++)
        {
            byte[] truncated = art[..length];
            Assert.Throws<InvalidDataException>(() => StaticArtDecoder.Decode(truncated));
        }
    }

    [Fact]
    public void SkiaPngRoundTripPreservesColorsAlphaAndNearestNeighborScaling()
    {
        var renderer = new SkiaAssetRenderer();
        using var png = SKBitmap.Decode(new ArtRenderer().RenderItemPng(Art(), 2));
        Assert.Equal((6, 4), (png.Width, png.Height));
        Assert.Equal((byte)0, png.GetPixel(0, 0).Alpha);
        Assert.Equal(SKColors.Lime, png.GetPixel(2, 0));
        Assert.Equal(SKColors.Lime, png.GetPixel(3, 1));
        Assert.Equal(SKColors.Blue, png.GetPixel(0, 2));
        Assert.Equal(SKColors.Black, png.GetPixel(5, 3));
        using var thumb = SKBitmap.Decode(renderer.RenderThumbnail(LandDecoder.Decode(Land()), 22, 22));
        Assert.Equal((22, 22), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void RendererCanDrawToAnExistingCanvasWithoutWebDependencies()
    {
        using var bitmap = new SKBitmap(3, 2);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
        new ArtRenderer().DrawItem(canvas, Art(), new SKRect(0, 0, 3, 2));
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
        using (var archive = ArtMulReader.Open(fixture.PathFor("artidx.mul"), fixture.PathFor("art.mul")))
        {
            Assert.Single(archive.LandEntries);
            Assert.Equal(0, Assert.Single(archive.ItemEntries).Id);
            Assert.Equal(44, archive.ReadLand(0).Width);
            Assert.Equal(3, archive.ReadItem(0).Width);
            Assert.Throws<KeyNotFoundException>(() => archive.ReadItem(-1));
            Assert.Throws<KeyNotFoundException>(() => archive.ReadItem(int.MaxValue));
            Assert.Throws<KeyNotFoundException>(() => archive.ReadLand(0x4000));
        }
        Entry(index, 0x4000, int.MaxValue, 20, 0);
        File.WriteAllBytes(fixture.PathFor("artidx.mul"), index);
        using var invalid = ArtMulReader.Open(fixture.PathFor("artidx.mul"), fixture.PathFor("art.mul"));
        Assert.Throws<InvalidDataException>(() => invalid.ReadItem(0));
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
        using (var reader = GumpMulReader.Open(fixture.PathFor("gumpidx.mul"), fixture.PathFor("gumpart.mul")))
        {
            Assert.Equal(3, reader.Read(0).Width);
            using var png = SKBitmap.Decode(new GumpRenderer().RenderPng(Gump(), 3, 2));
            Assert.Equal(SKColors.Red, png.GetPixel(0, 0));
            Assert.Equal((byte)0, png.GetPixel(1, 0).Alpha);
        }
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
        Assert.Throws<InvalidDataException>(() => IndexedMulFile.Open(fixture.PathFor("bad.idx"), fixture.PathFor("missing.mul")));
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
