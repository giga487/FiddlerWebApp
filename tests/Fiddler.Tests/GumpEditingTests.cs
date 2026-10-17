using System.Buffers.Binary;
using System.IO.Compression;
using Fiddler.Formats;
using Fiddler.Formats.Containers.Uop;
using Fiddler.Formats.Gumps;
using Fiddler.Graphics.Gumps;
using FiddlerWebApp.Services;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class GumpEditingTests
{
    private static GumpRecord Solid(int color, int width = 2, int height = 1) =>
        GumpEncoder.Encode(DecodedImage.FromArgb(width, height, Enumerable.Repeat(color, width * height).ToArray()));
    private const int Red = unchecked((int)0xFFFF0000), Blue = unchecked((int)0xFF0000FF), Green = unchecked((int)0xFF00FF00);

    [Fact]
    public void EncoderAndPngImportPreviewUseActualGumpColorsAndOneBitAlpha()
    {
        using var bitmap = new SKBitmap(3, 1);
        bitmap.SetPixel(0, 0, SKColors.Black);
        bitmap.SetPixel(1, 0, new SKColor(255, 0, 0, 127));
        bitmap.SetPixel(2, 0, new SKColor(0, 255, 0, 128));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var imported = GumpImageImporter.ImportPng(png.ToArray()).Decode();
        Assert.Equal(new[] { unchecked((int)0xFF080808), 0, Green }, imported.Pixels.ToArray());
        Assert.Throws<InvalidDataException>(() => GumpImageImporter.ImportPng([1, 2, 3]));
        using var oversized = new SKBitmap(4097, 1);
        using var oversizedImage = SKImage.FromBitmap(oversized);
        using var large = oversizedImage.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Throws<InvalidDataException>(() => GumpImageImporter.ImportPng(large.ToArray()));
    }

    [Fact]
    public void ChangesAddReplaceAndUndoWithoutMutatingSource()
    {
        var original = Solid(Red);
        using var document = new GumpDocument(new MemoryArchive(new() { [1] = original }));
        document.Put(1, Solid(Blue));
        document.Put(70000, Solid(Green));
        Assert.Equal(2, document.DirtyCount);
        Assert.Equal(Blue, document.Read(1).Pixels.Span[0]);
        Assert.Equal(Red, original.Decode().Pixels.Span[0]);
        Assert.Equal(new[] { 1, 70000 }, document.Entries.Select(entry => entry.Id));
        document.Undo(1); document.Undo(70000);
        Assert.False(document.HasChanges);
        Assert.Single(document.Entries);
        Assert.Equal(Red, document.Read(1).Pixels.Span[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Put(0x12000, original));
        Assert.Throws<InvalidDataException>(() => document.Put(2, new GumpRecord([], 2, 2)));
    }

    [Fact]
    public void ComparisonUsesDecodedPixelsNotEncodingAndDetectsAllDifferenceKinds()
    {
        using var document = new GumpDocument(new MemoryArchive(new() { [1] = Solid(Red), [2] = Solid(Blue), [3] = Solid(Green) }));
        byte[] split = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(split, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(4), 0x7C00);
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(8), 0x7C00);
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(10), 1);
        using var reference = new MemoryArchive(new() { [1] = new GumpRecord(split, 2, 1), [2] = Solid(Red), [4] = Solid(Green), [5] = new GumpRecord([], 1, 1) });
        var rows = GumpComparer.Compare(document, reference);
        Assert.Equal(new[] { GumpDifference.Equal, GumpDifference.Different, GumpDifference.CurrentOnly, GumpDifference.ReferenceOnly, GumpDifference.Invalid }, rows.Select(row => row.Difference));
        document.Put(2, reference.ReadRecord(2));
        Assert.Equal(GumpDifference.Equal, GumpComparer.Compare(document, reference).Single(row => row.Id == 2).Difference);
        Assert.Throws<OperationCanceledException>(() => GumpComparer.Compare(document, reference, new CancellationToken(true)));
    }

    [Fact]
    public void ExportPreservesOriginalsWritesCompleteNewPairAndKeepsHighIds()
    {
        using var folder = new TestFolder();
        WriteMul(folder.Path, new() { [1] = Solid(Red), [3] = Solid(Blue) });
        byte[] original = File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "gumpart.mul"));
        byte[] originalIndex = File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "gumpidx.mul"));
        using var document = GumpDocument.Open(folder.Path);
        document.Put(1, Solid(Green)); document.Put(70000, Solid(Red));
        string output = document.ExportMul(folder.Path);
        using var reopened = GumpMulReader.Open(System.IO.Path.Combine(output, "gumpidx.mul"), System.IO.Path.Combine(output, "gumpart.mul"));
        Assert.Equal(new[] { 1, 3, 70000 }, reopened.Entries.Select(entry => entry.Id));
        Assert.Equal(Green, reopened.Read(1).Pixels.Span[0]);
        Assert.Equal(Blue, reopened.Read(3).Pixels.Span[0]);
        Assert.Equal(Red, reopened.Read(70000).Pixels.Span[0]);
        Assert.Equal(70001 * 12, new FileInfo(System.IO.Path.Combine(output, "gumpidx.mul")).Length);
        Assert.Equal(original, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "gumpart.mul")));
        Assert.Equal(originalIndex, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "gumpidx.mul")));
        Assert.NotEqual(output, document.ExportMul(folder.Path));
        Assert.True(document.HasChanges);
    }

    [Fact]
    public void CancelledAndFailedExportsDoNotPublishPartialPairs()
    {
        using var folder = new TestFolder();
        using var document = new GumpDocument(new MemoryArchive(new() { [1] = Solid(Red) }));
        Assert.Throws<OperationCanceledException>(() => document.ExportMul(folder.Path, new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
        using var broken = new GumpDocument(new MemoryArchive(new() { [1] = new GumpRecord([], 1, 1) }));
        Assert.Throws<InvalidDataException>(() => broken.ExportMul(folder.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData(4, 0)] [InlineData(5, 0)] [InlineData(4, 1)] [InlineData(5, 1)] [InlineData(4, 3)] [InlineData(5, 3)]
    public void RenamedUopWithHeadersAndAllCompressionModesReadsAndCompares(int version, short compression)
    {
        using var folder = new TestFolder();
        string path = System.IO.Path.Combine(folder.Path, "renamed-reference.uop");
        var record = Solid(Red);
        WriteUop(path, [(1, record), (70000, Solid(Blue))], version, compression);
        using var reference = GumpArchive.Open(path);
        Assert.Equal(new[] { 1, 70000 }, reference.Entries.Select(entry => entry.Id));
        Assert.Equal(Red, reference.Read(1).Pixels.Span[0]);
        Assert.Equal(Blue, reference.Read(70000).Pixels.Span[0]);
        using var document = new GumpDocument(new MemoryArchive(new() { [1] = record, [70000] = Solid(Blue) }));
        Assert.All(GumpComparer.Compare(document, reference), row => Assert.Equal(GumpDifference.Equal, row.Difference));
        using var uopDocument = GumpDocument.Open(path);
        string output = uopDocument.ExportMul(folder.Path);
        using var exported = GumpArchive.Open(output);
        Assert.Equal(Red, exported.Read(1).Pixels.Span[0]);
    }

    [Fact]
    public void UopNameHashMatchesKnownClientVector()
    {
        Assert.Equal(0x126D1E99DDEDEE0AUL, UopNameHash.HashFileName("build/multicollection/housing.bin"));
    }

    [Fact]
    public void InvalidUopBlocksLengthsDuplicatesAndBrokenCompressionAreRejected()
    {
        using var folder = new TestFolder();
        string path = System.IO.Path.Combine(folder.Path, "bad.uop");
        WriteUop(path, [(1, Solid(Red))], 4, 1);
        var valid = File.ReadAllBytes(path);
        byte[] changed = valid.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(changed.AsSpan(44), 40);
        File.WriteAllBytes(path, changed);
        Assert.Throws<InvalidDataException>(() => UopArchive.Open(path));
        changed = valid.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(68), UopArchive.MaxPayload + 1);
        File.WriteAllBytes(path, changed);
        Assert.Throws<InvalidDataException>(() => UopArchive.Open(path));
        WriteUop(path, [(1, Solid(Red)), (1, Solid(Blue))], 4, 0);
        Assert.Throws<InvalidDataException>(() => UopArchive.Open(path));
        File.WriteAllBytes(path, valid[..^1]);
        Assert.Throws<InvalidDataException>(() => UopArchive.Open(path));
        changed = valid.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(68), 12); // deliberately short expanded length
        File.WriteAllBytes(path, changed);
        using var archive = GumpUopReader.Open(path);
        Assert.Throws<InvalidDataException>(() => archive.Read(1));
    }

    [Fact]
    public void UopIsUsedForCurrentFolderAndDraftsCannotBeDiscardedByAccidentalReopen()
    {
        using var folder = new TestFolder();
        WriteMul(folder.Path, new() { [1] = Solid(Blue) });
        WriteUop(System.IO.Path.Combine(folder.Path, "gumpartLegacyMUL.uop"), [(1, Solid(Red))], 4, 0);
        using var workspace = new MulWorkspace();
        workspace.Open(folder.Path);
        Assert.Equal("UOP", workspace.Gumps!.Format);
        Assert.Equal(Red, workspace.Gumps.Read(1).Pixels.Span[0]);
        workspace.Gumps.Put(1, Solid(Green));
        Assert.Throws<InvalidOperationException>(() => workspace.Open(folder.Path));
        Assert.Equal(Green, workspace.Gumps.Read(1).Pixels.Span[0]);
        string export = workspace.Gumps.ExportMul(folder.Path);
        workspace.Open(export, discardChanges: true);
        Assert.Equal("MUL / IDX", workspace.Gumps!.Format);
        Assert.Equal(Green, workspace.Gumps.Read(1).Pixels.Span[0]);
        Assert.False(workspace.Gumps.HasChanges);
    }

    internal static void WriteMul(string directory, Dictionary<int, GumpRecord> records)
    {
        Directory.CreateDirectory(directory);
        using var data = new BinaryWriter(File.Create(System.IO.Path.Combine(directory, "gumpart.mul")));
        using var index = new BinaryWriter(File.Create(System.IO.Path.Combine(directory, "gumpidx.mul")));
        int slots = records.Count == 0 ? 1 : records.Keys.Max() + 1;
        for (int id = 0; id < slots; id++)
        {
            if (!records.TryGetValue(id, out var record)) { index.Write(-1); index.Write(0); index.Write(0); continue; }
            index.Write((int)data.BaseStream.Position); index.Write(record.Data.Length); index.Write(record.Width << 16 | record.Height); data.Write(record.Data);
        }
    }

    internal static void WriteUop(string path, (int Id, GumpRecord Record)[] records, int version, short compression)
    {
        long firstBlock = version == 4 ? 40 : 512;
        var payloads = new List<(byte[] Bytes, int Expanded)>();
        foreach (var (_, record) in records)
        {
            var payload = new byte[8 + record.Data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(payload, record.Width);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), record.Height);
            record.Data.CopyTo(payload, 8);
            if (compression == 3) payload = MythicFixtureEncoder.Encode(payload);
            int expanded = payload.Length;
            if (compression != 0)
            {
                using var compressed = new MemoryStream();
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(payload);
                payload = compressed.ToArray();
            }
            payloads.Add((payload, expanded));
        }
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x50594DU); writer.Write((uint)version); writer.Write(0xFD23EC43U); writer.Write(firstBlock); writer.Write(100U); writer.Write(records.Length);
        writer.BaseStream.Position = firstBlock;
        writer.Write(records.Length); writer.Write(0L);
        long offset = firstBlock + 12 + records.Length * 34;
        for (int i = 0; i < records.Length; i++)
        {
            writer.Write(offset); writer.Write(12); writer.Write(payloads[i].Bytes.Length); writer.Write(payloads[i].Expanded);
            writer.Write(UopNameHash.HashFileName($"build/gumpartlegacymul/{records[i].Id:D8}.tga"));
            writer.Write(0U); writer.Write(compression);
            offset += 12 + payloads[i].Bytes.Length;
        }
        foreach (var payload in payloads) { writer.Write(new byte[12]); writer.Write(payload.Bytes); }
    }

    private sealed class MemoryArchive(Dictionary<int, GumpRecord> records) : IGumpArchive
    {
        public IReadOnlyList<MulEntry> Entries { get; } = records.Select(pair => new MulEntry(AssetKind.Gump, pair.Key, 0, pair.Value.Data.Length, pair.Value.Width << 16 | pair.Value.Height)).ToArray();
        public int SlotCount => records.Count == 0 ? 0 : records.Keys.Max() + 1;
        public GumpRecord ReadRecord(int id) => records[id];
        public DecodedImage Read(int id) => records[id].Decode();
        public void Dispose() { }
    }
    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("fiddler-gump-edit-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
