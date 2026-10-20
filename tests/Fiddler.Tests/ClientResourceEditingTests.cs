using System.Buffers.Binary;
using System.Text;
using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Fonts;
using Fiddler.Formats.Hues;
using Fiddler.Formats.Localization;
using Fiddler.Formats.Metadata;
using Fiddler.Formats.Catalog;
using FiddlerWebApp.Services;
using Xunit;

namespace Fiddler.Tests;

public sealed class ClientResourceEditingTests
{
    private const int Red = unchecked((int)0xFFFF0000), Black = unchecked((int)0xFF000000);

    [Fact]
    public void FileScanExplainsTheNewEditorsInsteadOfMarkingTheirDataUnused()
    {
        using var files = new Files();
        var report = new ClientDirectoryScanner().Scan(files.Root);
        foreach (string name in new[] { "fonts.mul", "unifont.mul", "cliloc.enu", "tiledata.mul" })
        {
            var entry = report.Files.Single(file => file.Name == name);
            Assert.Equal(ClientFileImportance.RequiredForFeature, entry.Guidance!.Importance);
            Assert.DoesNotContain("not consumed", entry.Guidance.Impact);
        }
    }

    [Fact]
    public void ClilocRoundTripPreservesHeaderFlagsOrderAndLongUtf8Strings()
    {
        using var files = new Files();
        string longText = new('é', 20000);
        byte[] original = ClilocBytes(new(300, 7, "~1_NAME~ says hello"), new(1, 255, longText));
        string path = files.Write("cliloc.enu", original);
        var document = ClilocDocument.Open(path);
        string unchanged = document.Export(files.Root);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(unchanged, "cliloc.enu")));
        document.Put(new(300, 7, "Ciao 🌊 ~1_NAME~")); document.Put(new(500, 2, "New entry"));
        var exported = ClilocDocument.Open(Path.Combine(document.Export(files.Root), "cliloc.enu"));
        Assert.Equal("Ciao 🌊 ~1_NAME~", exported.Entries.Single(e => e.Id == 300).Text);
        Assert.Equal((byte)255, exported.Entries.Single(e => e.Id == 1).Flag);
        Assert.Equal(longText, exported.Entries.Single(e => e.Id == 1).Text);
        Assert.Equal(3, exported.Entries.Count());
        document.Remove(1);
        var removed = ClilocDocument.Open(Path.Combine(document.Export(files.Root), "cliloc.enu"));
        Assert.DoesNotContain(removed.Entries, e => e.Id == 1);
        document.Undo(1); document.Undo(500); document.Undo(300);
        Assert.Equal(0, document.DirtyCount);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Throws<ArgumentException>(() => document.Put(new(1, 0, new string('é', 32768))));
    }

    [Fact]
    public void ClilocRejectsTruncationAndDuplicateIdsInsteadOfSavingPartialData()
    {
        using var files = new Files();
        byte[] valid = ClilocBytes(new ClilocEntry(1, 0, "text"));
        Assert.Throws<InvalidDataException>(() => ClilocDocument.Open(files.Write("cliloc.enu", valid[..^1])));
        Assert.Throws<InvalidDataException>(() => ClilocDocument.Open(files.Write("cliloc.enu", ClilocBytes(new(1, 0, "a"), new(1, 0, "b")))));
    }

    [Theory]
    [InlineData(TileDataLayout.Legacy32)]
    [InlineData(TileDataLayout.Extended64)]
    public void TileDataRoundTripPreservesHeadersAndUnknownFieldsInBothLayouts(TileDataLayout layout)
    {
        using var files = new Files();
        byte[] original = TileDataBytes(layout);
        string path = files.Write("tiledata.mul", original);
        var document = TileDataDocument.Open(path);
        Assert.Equal(layout, document.Layout);
        Assert.Equal(32, document.ItemCount);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(document.Export(files.Root), "tiledata.mul")));
        var item = document.Read(AssetKind.Item, 3);
        Assert.Equal((short)-123, item.MiscData); Assert.Equal((byte)91, item.Unknown2); Assert.Equal((byte)92, item.Unknown3);
        ulong flags = layout == TileDataLayout.Extended64 ? (1UL << 63) | 0x40000 : 0x40000;
        document.Put(item with { Name = "Edited item", Flags = flags, Animation = 60000, Height = 20 });
        document.Put(document.Read(AssetKind.Land, 5) with { Name = "Edited land", TextureId = 123 });
        var exported = TileDataDocument.Open(Path.Combine(document.Export(files.Root), "tiledata.mul"));
        var edited = exported.Read(AssetKind.Item, 3);
        Assert.Equal("Edited item", edited.Name); Assert.Equal(flags, edited.Flags); Assert.True(edited.PartialHue);
        Assert.Equal((ushort)60000, edited.Animation); Assert.Equal(item.Unknown2, edited.Unknown2); Assert.Equal(item.Unknown3, edited.Unknown3);
        Assert.Equal((ushort)123, exported.Read(AssetKind.Land, 5).TextureId);
        Assert.Equal(document.Read(AssetKind.Item, 4), exported.Read(AssetKind.Item, 4));
        Assert.Equal(original, File.ReadAllBytes(path));
        document.Undo(AssetKind.Item, 3); document.Undo(AssetKind.Land, 5);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(document.Export(files.Root), "tiledata.mul")));
        Assert.Throws<ArgumentException>(() => document.Put(item with { Name = "This name is too long!" }));
        if (layout == TileDataLayout.Legacy32) Assert.Throws<ArgumentException>(() => document.Put(item with { Flags = 1UL << 32 }));
        Assert.Throws<InvalidDataException>(() => TileDataDocument.Open(files.Write("bad.mul", original[..^1]), layout));
    }

    [Fact]
    public void AutomaticHueModeUsesCurrentFolderAndMetadataDrafts()
    {
        using var files = new Files();
        files.Write("tiledata.mul", TileDataBytes(TileDataLayout.Extended64));
        var workspace = new ClientResourceWorkspace();
        Assert.Equal(HueMode.Full, workspace.HueModeFor(files.Root, AssetKind.Item, 3));
        var document = workspace.TileData!;
        document.Put(document.Read(AssetKind.Item, 3) with { Flags = 0x40000 });
        Assert.Equal(HueMode.GrayPixelsOnly, workspace.HueModeFor(files.Root, AssetKind.Item, 3));
        Assert.Equal(HueMode.Full, workspace.HueModeFor(files.Root, AssetKind.Gump, 3));
        string other = Directory.CreateDirectory(Path.Combine(files.Root, "other")).FullName;
        Assert.Equal(HueMode.Full, workspace.HueModeFor(other, AssetKind.Item, 3));
        Assert.Throws<InvalidOperationException>(() => workspace.OpenTileData(files.Root));
        document.Discard(); Assert.Equal(HueMode.Full, workspace.HueModeFor(files.Root, AssetKind.Item, 3));
    }

    [Fact]
    public void UnicodeGlyphRoundTripHandlesBitRowsOffsetsNewAndEmptyGlyphs()
    {
        using var files = new Files();
        byte[] source = UnicodeBytes();
        string path = files.Write("unifont.mul", source);
        var document = FontDocument.Open(path);
        Assert.True(document.Unicode);
        var glyph = document.Read(0, 65);
        Assert.Equal((sbyte)-2, glyph.XOffset); Assert.Equal((sbyte)3, glyph.YOffset);
        Assert.Equal(new[] { Black, 0, Black, 0, Black, 0, Black, 0, Black }, document.Image(0, 65).Pixels.ToArray());
        document.Read(0, 65).Data[0] = 0;
        Assert.Equal(Black, document.Image(0, 65).Pixels.Span[0]);
        int[] pixels = [Black, 0, Red, 0, Black, 0, Red, 0, Black, 0, Black, 0, Black, 0, Black, 0, Black, 0];
        document.Put(0, 66, DecodedImage.FromArgb(9, 2, pixels), -3, 4);
        document.SetOffsets(0, 65, -5, -6);
        document.Clear(0, 67);
        var reopened = FontDocument.Open(Path.Combine(document.Export(files.Root), "unifont.mul"));
        Assert.Equal(pixels.Select(p => p == 0 ? 0 : Black), reopened.Image(0, 66).Pixels.ToArray());
        Assert.Equal((sbyte)-3, reopened.Read(0, 66).XOffset); Assert.Equal((sbyte)4, reopened.Read(0, 66).YOffset);
        Assert.Equal((sbyte)-5, reopened.Read(0, 65).XOffset); Assert.Equal((sbyte)-6, reopened.Read(0, 65).YOffset);
        Assert.Equal(0, reopened.Read(0, 67).Width);
        Assert.Equal(source, File.ReadAllBytes(path));
        var text = reopened.RenderText(0, "AB\nBA"); Assert.True(text.Width > 9); Assert.True(text.Pixels.Span.Contains(Black));
        document.Discard(); Assert.Equal(0, document.DirtyCount);
        Assert.Equal((sbyte)-2, document.Read(0, 65).XOffset);
        Assert.Throws<ArgumentException>(() => document.Put(0, 65, DecodedImage.FromArgb(256, 1, new int[256])));
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(65 * 4), source.Length - 1);
        Assert.Throws<InvalidDataException>(() => FontDocument.Open(files.Write("unifont1.mul", source)));
    }

    [Fact]
    public void AsciiFontPreservesHeadersUnknownsAndUntouchedGlyphs()
    {
        using var files = new Files();
        byte[] source = AsciiBytes();
        string path = files.Write("fonts.mul", source);
        var document = FontDocument.Open(path);
        Assert.False(document.Unicode); Assert.Single(document.Fonts);
        Assert.Equal((byte)17, document.Read(0, 65).Unknown);
        Assert.Equal(Red, document.Image(0, 65).Pixels.Span[0]);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(document.Export(files.Root), "fonts.mul")));
        document.Put(0, 66, DecodedImage.FromArgb(2, 1, [Red, 0]));
        document.Clear(0, 65);
        var reopened = FontDocument.Open(Path.Combine(document.Export(files.Root), "fonts.mul"));
        Assert.Equal(new[] { Red, 0 }, reopened.Image(0, 66).Pixels.ToArray());
        Assert.Equal((byte)17, reopened.Read(0, 65).Unknown);
        Assert.Equal(0, reopened.Read(0, 65).Width);
        document.Undo(0, 65); Assert.Equal(Red, document.Image(0, 65).Pixels.Span[0]);
        Assert.Equal(source, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => FontDocument.Open(files.Write("fonts.mul", source[..^2])));
    }

    [Fact]
    public void ItemAndLandEncodingRoundTripMatchesStoredPixelsAndTransparency()
    {
        var random = new Random(42);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int width = random.Next(1, 70), height = random.Next(1, 50);
            var pixels = Enumerable.Range(0, width * height).Select(_ => random.Next(3) == 0 ? 0 : random.Next() | unchecked((int)0xFF000000)).ToArray();
            var image = DecodedImage.FromArgb(width, height, pixels);
            var decoded = StaticArtDecoder.Decode(ArtEncoder.Item(image));
            Assert.Equal(pixels.Select(p => (uint)p >> 24 == 0 ? 0 : HuePalette.ToArgb(HuePalette.FromRgb((p >> 16) & 255, (p >> 8) & 255, p & 255))), decoded.Pixels.ToArray());
        }
        var land = LandDecoder.Decode(ArtEncoder.Land(DecodedImage.FromArgb(44, 44, Enumerable.Repeat(Red, 44 * 44).ToArray())));
        Assert.Equal(1012, land.Pixels.ToArray().Count(p => p == Red));
        Assert.Equal(0, land.Pixels.Span[0]);
        Assert.Throws<ArgumentException>(() => ArtEncoder.Land(DecodedImage.FromArgb(2, 2, new int[4])));
    }

    [Fact]
    public void ArtEditsExportFullPairPreserveUnchangedRecordsAndStayInSession()
    {
        using var files = new Files();
        var item = DecodedImage.FromArgb(2, 1, [Red, Black]);
        byte[] encoded = ArtEncoder.Item(item);
        string index = files.Write("artidx.mul", new byte[(0x4002) * 12]);
        string data = files.Write("art.mul", encoded);
        using (var writer = new BinaryWriter(File.OpenWrite(index)))
        {
            for (int slot = 0; slot < 0x4002; slot++) { writer.Write(slot == 0x4001 ? 0 : -1); writer.Write(slot == 0x4001 ? encoded.Length : 0); writer.Write(slot == 0x4001 ? 99 : 0); }
        }
        byte[] originalIndex = File.ReadAllBytes(index);
        using var first = new MulWorkspace(); using var second = new MulWorkspace();
        first.Open(files.Root); second.Open(files.Root);
        first.Art!.Put(AssetKind.Item, 2, DecodedImage.FromArgb(1, 1, [Black]));
        first.Art.Put(AssetKind.Land, 3, DecodedImage.FromArgb(44, 44, Enumerable.Repeat(Red, 44 * 44).ToArray()));
        Assert.Contains(first.Entries(AssetKind.Item), e => e.Id == 2);
        Assert.DoesNotContain(second.Entries(AssetKind.Item), e => e.Id == 2);
        Assert.Throws<InvalidOperationException>(() => first.Open(files.Root));
        string output = first.Art.Export(files.Root);
        using (var exported = ArtDocument.Open(Path.Combine(output, "artidx.mul"), Path.Combine(output, "art.mul")))
        {
            Assert.Equal(item.Pixels.ToArray(), exported.Read(AssetKind.Item, 1).Pixels.ToArray());
            Assert.Equal(99, exported.Entries(AssetKind.Item).Single(e => e.Id == 1).Extra);
            Assert.Equal(Black, exported.Read(AssetKind.Item, 2).Pixels.Span[0]);
            Assert.Equal(1012, exported.Read(AssetKind.Land, 3).Pixels.ToArray().Count(p => p == Red));
        }
        first.Art.Remove(AssetKind.Item, 1);
        Assert.DoesNotContain(first.Entries(AssetKind.Item), e => e.Id == 1);
        first.Art.Undo(AssetKind.Item, 1);
        Assert.Contains(first.Entries(AssetKind.Item), e => e.Id == 1);
        Assert.Equal(originalIndex, File.ReadAllBytes(index)); Assert.Equal(encoded, File.ReadAllBytes(data));
    }

    internal static byte[] ClilocBytes(params ClilocEntry[] entries)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(0x12345678); writer.Write((short)4);
        foreach (var entry in entries) { byte[] text = Encoding.UTF8.GetBytes(entry.Text); writer.Write(entry.Id); writer.Write(entry.Flag); writer.Write((ushort)text.Length); writer.Write(text); }
        return memory.ToArray();
    }
    internal static byte[] TileDataBytes(TileDataLayout layout)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        for (int group = 0; group < 513; group++)
        {
            writer.Write(0x12345678 + group);
            for (int i = 0; i < 32; i++)
            {
                if (layout == TileDataLayout.Extended64) writer.Write(0UL); else writer.Write(0U);
                if (group < 512) writer.Write((ushort)12);
                else
                {
                    writer.Write((byte)1); writer.Write((byte)2); writer.Write((short)-123); writer.Write((byte)91); writer.Write((byte)4);
                    writer.Write((ushort)5); writer.Write((byte)92); writer.Write((byte)6); writer.Write((byte)7); writer.Write((byte)8); writer.Write((byte)9);
                }
                var name = new byte[20]; Encoding.ASCII.GetBytes(group < 512 ? "Land" : "Item").CopyTo(name, 0); writer.Write(name);
            }
        }
        return memory.ToArray();
    }
    internal static byte[] UnicodeBytes()
    {
        byte[] data = new byte[0x40000 + 6]; BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(65 * 4), 0x40000);
        data[0x40000] = 254; data[0x40001] = 3; data[0x40002] = 9; data[0x40003] = 1; data[0x40004] = 0xAA; data[0x40005] = 0x80; return data;
    }
    internal static byte[] AsciiBytes()
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory); writer.Write((byte)1);
        for (int code = 32; code < 256; code++) { writer.Write((byte)(code == 65 ? 1 : 0)); writer.Write((byte)(code == 65 ? 1 : 0)); writer.Write((byte)17); if (code == 65) writer.Write((ushort)0x7C00); }
        writer.Write((byte)0); return memory.ToArray();
    }
    internal sealed class Files : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fiddler-resource-" + Guid.NewGuid().ToString("N"))).FullName;
        public string Write(string name, byte[] data) { string path = Path.Combine(Root, name); File.WriteAllBytes(path, data); return path; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
