using System.Buffers.Binary;
using System.IO.Compression;
using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Containers.Uop;
using Fiddler.Formats.Maps;
using Fiddler.Formats.Multis;
using Fiddler.Formats.Catalog;
using Fiddler.Graphics.World;
using FiddlerWebApp.Services;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class WorldAssetTests
{
    [Theory]
    [InlineData(12)] [InlineData(16)]
    public void MulMultiPreservesSignedCoordinatesFlagsAndExtra(int rowSize)
    {
        using var folder = new TestFolder();
        byte[] data = MultiBytes(rowSize, (1, -2, 3, -4, 1, 9), (2, 0, 0, 0, 0, 10));
        WriteIndexed(folder.Path, "multi.idx", "multi.mul", new() { [44] = data });
        using var multi = MultiArchive.Open(folder.File("multi.mul"), folder.File("multi.idx"), rowSize);
        Assert.Equal([44], multi.Ids);
        var rows = multi.Read(44);
        Assert.Equal(new MultiComponent(0, 1, -2, 3, -4, 1, rowSize == 16 ? 9 : 0), rows[0]);
        Assert.Equal(0, rows[1].Flags);
        Assert.Throws<KeyNotFoundException>(() => multi.Read(43));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void UopMultiSkipsClilocsAndMapsVisibilityBits(int flag)
    {
        using var folder = new TestFolder();
        var payload = UopMultiBytes(44);
        WriteUop(folder.File("renamed.uop"), [("build/multicollection/000044.bin", payload), ("build/multicollection/housing.bin", new byte[8])], (short)flag);
        using var multi = MultiArchive.Open(folder.File("renamed.uop"));
        Assert.Equal([44], multi.Ids);
        var rows = multi.Read(44);
        Assert.Equal(new MultiComponent(0, 1, -2, 3, -4, 1, 1), rows[0]);
        Assert.Equal(0, rows[1].Flags);
    }

    [Fact]
    public void CorruptMultiRowsAndUopClilocLengthsAreRejected()
    {
        using var folder = new TestFolder();
        WriteIndexed(folder.Path, "multi.idx", "multi.mul", new() { [0] = new byte[13] });
        using (var multi = MultiArchive.Open(folder.File("multi.mul"), folder.File("multi.idx"), 12))
            Assert.Throws<InvalidDataException>(() => multi.Read(0));
        var payload = UopMultiBytes(44);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(18), int.MaxValue);
        WriteUop(folder.File("multi.uop"), [("build/multicollection/000044.bin", payload)]);
        using var uop = MultiArchive.Open(folder.File("multi.uop"));
        Assert.Throws<InvalidDataException>(() => uop.Read(44));
    }

    [Fact]
    public void MapReadsAcrossBothBlockAxesWithCorrectTileOrderAndSignedZ()
    {
        using var folder = new TestFolder();
        File.WriteAllBytes(folder.File("map0.mul"), MapBytes());
        WriteIndexed(folder.Path, "staidx0.mul", "statics0.mul", new() { [2] = StaticBytes(1, 0, 7, -12, 77) }, 4);
        using var map = MapArchive.Open(folder.File("map0.mul"), 0, folder.File("staidx0.mul"), folder.File("statics0.mul"));
        var region = map.ReadRegion(16, 16, 7, 7, 4);
        Assert.Equal(16, region.Land.Count);
        Assert.Equal(0, region.Land.Single(t => t.X == 7 && t.Y == 7).TileId);
        Assert.Equal(2, region.Land.Single(t => t.X == 8 && t.Y == 7).TileId);
        Assert.Equal(1, region.Land.Single(t => t.X == 7 && t.Y == 8).TileId);
        Assert.Equal(-5, region.Land.Single(t => t.X == 8 && t.Y == 8).Z);
        Assert.Equal(new MapStaticTile(8, 7, 1, -12, 77), Assert.Single(region.Statics));
        Assert.Throws<ArgumentException>(() => map.ReadRegion(16, 16, 15, 15, 4));
        Assert.Throws<InvalidDataException>(() => map.ReadRegion(32, 16, 0, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void MapUopMatchesMulWithoutUsingExternalFilename(int flag)
    {
        using var folder = new TestFolder();
        WriteUop(folder.File("renamed.uop"), [("build/map3legacymul/00000000.dat", MapBytes())], (short)flag);
        using var map = MapArchive.Open(folder.File("renamed.uop"), 3);
        Assert.Equal(4, map.BlockCount);
        var region = map.ReadRegion(16, 16, 8, 8, 4);
        Assert.All(region.Land, tile => Assert.Equal(3, tile.TileId));
        Assert.False(map.HasStatics);
    }

    [Fact]
    public void MapUopCrossesChunkBoundaryAndRejectsGaps()
    {
        using var folder = new TestFolder();
        byte[] first = new byte[4096 * 196];
        byte[] second = new byte[4096 * 196];
        BinaryPrimitives.WriteUInt16LittleEndian(second.AsSpan(4), 77);
        WriteUop(folder.File("map.uop"), [("build/map0legacymul/00000001.dat", second), ("build/map0legacymul/00000000.dat", first)], 1);
        using (var map = MapArchive.Open(folder.File("map.uop")))
        {
            var region = map.ReadRegion(128, 4096, 64, 0, 1);
            Assert.Equal(77, Assert.Single(region.Land).TileId);
        }
        WriteUop(folder.File("gap.uop"), [("build/map0legacymul/00000001.dat", second)]);
        Assert.Throws<InvalidDataException>(() => MapArchive.Open(folder.File("gap.uop")));
    }

    [Fact]
    public void MapRejectsMisalignedBlocksAndBadStaticCoordinates()
    {
        using var folder = new TestFolder();
        File.WriteAllBytes(folder.File("bad.mul"), new byte[197]);
        Assert.Throws<InvalidDataException>(() => MapArchive.Open(folder.File("bad.mul")));
        File.WriteAllBytes(folder.File("map0.mul"), MapBytes());
        WriteIndexed(folder.Path, "staidx0.mul", "statics0.mul", new() { [0] = StaticBytes(1, 9, 0, 0, 0) }, 4);
        using var map = MapArchive.Open(folder.File("map0.mul"), 0, folder.File("staidx0.mul"), folder.File("statics0.mul"));
        Assert.Throws<InvalidDataException>(() => map.ReadRegion(16, 16, 0, 0));
        Assert.Equal(64, map.ReadRegion(16, 16, 0, 0, includeStatics: false).Land.Count);
    }

    [Fact]
    public void SizeDetectionIsFacetSpecificAndAcceptsOneTrailingBlock()
    {
        Assert.Equal((2560, 2048), MapArchive.DetectSize(3, 81920));
        Assert.Equal((1280, 4096), MapArchive.DetectSize(5, 81921));
        Assert.Null(MapArchive.DetectSize(0, 4));
    }

    [Fact]
    public void EmptyStaticsIndexMeansNoStaticsAndShortNonemptyIndexIsRejected()
    {
        using var folder = new TestFolder();
        File.WriteAllBytes(folder.File("map0.mul"), MapBytes());
        File.WriteAllBytes(folder.File("staidx0.mul"), []);
        File.WriteAllBytes(folder.File("statics0.mul"), []);
        using (var map = MapArchive.Open(folder.File("map0.mul"), 0, folder.File("staidx0.mul"), folder.File("statics0.mul")))
        { Assert.False(map.HasStatics); Assert.Empty(map.ReadRegion(16, 16, 0, 0).Statics); }
        WriteIndexed(folder.Path, "staidx0.mul", "statics0.mul", new() { [0] = StaticBytes(1, 0, 0, 0, 0) });
        using var incomplete = MapArchive.Open(folder.File("map0.mul"), 0, folder.File("staidx0.mul"), folder.File("statics0.mul"));
        Assert.Throws<InvalidDataException>(() => incomplete.ReadRegion(16, 16, 0, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void ArtUopResolvesLandAndOffsetStaticIds(int flag)
    {
        using var folder = new TestFolder();
        WriteUop(folder.File("art.uop"), [("build/artlegacymul/00000000.tga", LandBytes(0x7C00)), ("build/artlegacymul/00016385.tga", ArtBytes(0x03E0))], (short)flag);
        using var art = ArtArchive.Open(folder.File("art.uop"));
        Assert.Equal(44, art.Read(0, true).Width);
        Assert.Equal(unchecked((int)0xFF00FF00), art.Read(1, false).Pixels.Span[0]);
    }

    [Fact]
    public void SceneProjectionHitTestingMissingArtAndBoundsAreVisible()
    {
        var renderer = new IsometricSceneRenderer();
        var image = DecodedImage.FromArgb(2, 2, [unchecked((int)0xFFFF0000), 0, 0, 0]);
        WorldTile first = new(0, 1, 0, 0, 0, false), upper = new(1, 2, 1, 0, 1, false);
        var scene = renderer.Render([first, upper], (_, _) => image);
        var a = scene.Hits[0]; var b = scene.Hits[1];
        Assert.Equal(22, b.X - a.X);
        Assert.Equal(18, b.Y - a.Y);
        Assert.Equal(first, scene.HitTest(a.X, a.Y));
        Assert.Null(scene.HitTest(a.X + 1, a.Y));
        using var png = SKBitmap.Decode(scene.Png);
        Assert.Equal(scene.Width, png.Width);
        var missing = renderer.Render([first], (_, _) => throw new KeyNotFoundException("missing"));
        Assert.Single(missing.Warnings);
        Assert.Single(missing.Hits);
        Assert.Throws<InvalidDataException>(() => renderer.Render([first, upper with { X = 200 }], (_, _) => image));
    }

    [Fact]
    public void WorkspaceOpensEitherSourceAndFailedOpenPreservesActiveReaders()
    {
        using var folder = new TestFolder();
        WriteSceneFixtures(folder.Path);
        using var workspace = new WorldAssetWorkspace();
        workspace.OpenMultis(folder.Path, null, 16);
        Assert.Contains("UOP", workspace.Multis!.Format);
        Assert.Equal(2, workspace.Multis.Read(44).Count);
        Assert.Throws<FileNotFoundException>(() => workspace.OpenMap(folder.Path, 1, false, false));
        Assert.Equal(2, workspace.Multis.Read(44).Count);
        workspace.OpenMap(folder.Path, 0, false, true);
        Assert.Null(workspace.Multis);
        Assert.Equal(64, workspace.Map!.ReadRegion(16, 16, 0, 0).Land.Count);
        Assert.NotNull(workspace.ReadArt(0, true));
        var report = new ClientDirectoryScanner().Scan(folder.Path);
        Assert.True(report.Groups.Single(g => g.Definition.Name == "Multi").CanView);
        Assert.True(report.Groups.Single(g => g.Definition.Name == "Map 0").CanView);
    }

    internal static void WriteSceneFixtures(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        WriteIndexed(directory, "multi.idx", "multi.mul", new() { [44] = MultiBytes(16, (1, 0, 0, 0, 1, 0), (1, 1, 0, 0, 1, 0)), [45] = MultiBytes(16, (1, 0, 0, 2, 1, 0)) });
        WriteIndexed(directory, "artidx.mul", "art.mul", new() { [0] = LandBytes(0x03E0), [1] = LandBytes(0x7C00), [2] = LandBytes(0x001F), [3] = LandBytes(0x7FE0), [0x4001] = ArtBytes(0x7C00) });
        File.WriteAllBytes(Path.Combine(directory, "map0.mul"), MapBytes());
        WriteIndexed(directory, "staidx0.mul", "statics0.mul", new() { [0] = StaticBytes(1, 2, 3, 1, 5) }, 4);
        WriteUop(Path.Combine(directory, "MultiCollection.uop"), [("build/multicollection/000044.bin", UopMultiBytes(44))], 1);
        WriteUop(Path.Combine(directory, "map0LegacyMUL.uop"), [("build/map0legacymul/00000000.dat", MapBytes())], 1);
    }
    internal static void WriteUop(string path, (string Name, byte[] Payload)[] records, short flag = 0)
    {
        var payloads = records.Select(r =>
        {
            if (flag == 0) return r.Payload;
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true)) zlib.Write(r.Payload);
            return output.ToArray();
        }).ToArray();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x50594DU); writer.Write(5U); writer.Write(0U); writer.Write(512L); writer.Write(records.Length); writer.Write(records.Length);
        writer.BaseStream.Position = 512;
        writer.Write(records.Length); writer.Write(0L);
        long offset = 512 + 12 + records.Length * 34;
        for (int i = 0; i < records.Length; i++)
        {
            writer.Write(offset); writer.Write(0); writer.Write(payloads[i].Length); writer.Write(records[i].Payload.Length);
            writer.Write(UopNameHash.HashFileName(records[i].Name)); writer.Write(0); writer.Write(flag);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads) writer.Write(payload);
    }
    private static byte[] MultiBytes(int size, params (int Id, int X, int Y, int Z, int Flags, int Extra)[] rows)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        foreach (var row in rows) { writer.Write((ushort)row.Id); writer.Write((short)row.X); writer.Write((short)row.Y); writer.Write((short)row.Z); writer.Write(row.Flags); if (size == 16) writer.Write(row.Extra); }
        return output.ToArray();
    }
    private static byte[] UopMultiBytes(int id)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(id); writer.Write(2);
        writer.Write((ushort)1); writer.Write((short)-2); writer.Write((short)3); writer.Write((short)-4); writer.Write((ushort)0x100); writer.Write(2); writer.Write(100); writer.Write(200);
        writer.Write((ushort)1); writer.Write((short)0); writer.Write((short)0); writer.Write((short)0); writer.Write((ushort)1); writer.Write(0);
        return output.ToArray();
    }
    private static byte[] MapBytes()
    {
        byte[] data = new byte[196 * 4];
        for (int b = 0; b < 4; b++) for (int i = 0; i < 64; i++)
        { BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(b * 196 + 4 + i * 3), (ushort)b); data[b * 196 + 6 + i * 3] = unchecked((byte)-5); }
        return data;
    }
    private static byte[] StaticBytes(int id, byte x, byte y, sbyte z, ushort hue)
    {
        byte[] bytes = new byte[7]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)id);
        bytes[2] = x; bytes[3] = y; bytes[4] = unchecked((byte)z); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(5), hue); return bytes;
    }
    private static byte[] LandBytes(ushort color)
    { byte[] bytes = new byte[2024]; for (int p = 0; p < bytes.Length; p += 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), color); return bytes; }
    private static byte[] ArtBytes(ushort color)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(0); writer.Write((ushort)22); writer.Write((ushort)44);
        for (int y = 0; y < 44; y++) writer.Write((ushort)(y * 26));
        for (int y = 0; y < 44; y++) { writer.Write((ushort)0); writer.Write((ushort)22); for (int x = 0; x < 22; x++) writer.Write(color); writer.Write(0); }
        return output.ToArray();
    }
    private static void WriteIndexed(string folder, string indexName, string dataName, Dictionary<int, byte[]> records, int? slots = null)
    {
        using var index = new BinaryWriter(File.Create(Path.Combine(folder, indexName)));
        using var data = new BinaryWriter(File.Create(Path.Combine(folder, dataName)));
        for (int id = 0; id < (slots ?? records.Keys.Max() + 1); id++)
        {
            if (!records.TryGetValue(id, out var bytes)) { index.Write(-1); index.Write(0); index.Write(0); continue; }
            index.Write((int)data.BaseStream.Position); index.Write(bytes.Length); index.Write(0); data.Write(bytes);
        }
    }
    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateTempSubdirectory("fiddler-world-").FullName;
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => System.IO.Directory.Delete(Path, true);
    }
}
