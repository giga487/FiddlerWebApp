using System.Buffers.Binary;
using Fiddler.Formats;
using Fiddler.Formats.Hues;
using FiddlerWebApp.Services;
using Xunit;

namespace Fiddler.Tests;

public sealed class HueTests
{
    private static ushort[] RedRamp() => Enumerable.Range(0, 32).Select(i => (ushort)(i << 10)).ToArray();

    [Fact]
    public void FullAndPartialMappingUseRedFiveBitsAndPreserveAlphaAndOriginalPixels()
    {
        int gray = HuePalette.ToArgb(0x4210), green = HuePalette.ToArgb(0x03E0);
        var original = DecodedImage.FromArgb(5, 1, [0x123456, gray, green, unchecked((int)0x80424242), unchecked((int)0xFF000000)]);
        var full = HuePalette.Apply(original, RedRamp(), HueMode.Full).Pixels.ToArray();
        var partial = HuePalette.Apply(original, RedRamp(), HueMode.GrayPixelsOnly).Pixels.ToArray();
        Assert.Equal(0x123456, full[0]); // Hidden RGB remains hidden.
        Assert.Equal(HuePalette.ToArgb(16 << 10), full[1]);
        Assert.Equal(unchecked((int)0xFF000000), full[2]); // Red channel, rather than perceived brightness.
        Assert.Equal(0x80420000u, (uint)full[3]);
        Assert.Equal(unchecked((int)0xFF000000), full[4]); // Opaque black stays opaque.
        Assert.Equal(green, partial[2]);
        Assert.Equal(full[1], partial[1]);
        Assert.Equal(gray, original.Pixels.Span[1]);
        Assert.Throws<ArgumentException>(() => HuePalette.Apply(original, new ushort[31], HueMode.Full));
    }

    [Fact]
    public void GradientIncludesAllThreeAnchorsAndProducesValidGameColors()
    {
        ushort dark = HuePalette.FromRgb(12, 24, 48), mid = HuePalette.FromRgb(42, 128, 200), light = HuePalette.FromRgb(240, 248, 255);
        ushort[] colors = HuePalette.Gradient(dark, mid, light);
        Assert.Equal(32, colors.Length);
        Assert.Equal(dark, colors[0]); Assert.Equal(mid, colors[15]); Assert.Equal(light, colors[31]);
        Assert.All(colors, color => Assert.InRange(color, (ushort)0, (ushort)0x7FFF));
        Assert.Equal(0x7FFF, HuePalette.FromRgb(255, 255, 255));
        Assert.Equal(unchecked((int)0xFFFFFFFF), HuePalette.ToArgb(0x7FFF));
        for (ushort c = 0; c < 32; c++)
        {
            int rgb = HuePalette.ToArgb((ushort)(c << 10 | c << 5 | c));
            Assert.Equal((ushort)(c << 10 | c << 5 | c), HuePalette.FromRgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
        }
    }

    [Fact]
    public void ReaderUsesGameIdsAndNoOpExportPreservesEverySourceByte()
    {
        using var folder = new TempFolder();
        byte[] source = Fixture();
        string path = folder.Write(source);
        var document = HueDocument.Open(path);
        Assert.Equal(8, document.Count);
        Assert.Equal("First hue", document.Read(1).Name);
        Assert.Equal((ushort)31, document.Read(1).Colors.Span[31]);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Read(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Read(9));
        Assert.False(document.HasChanges);
        Assert.Equal(source, File.ReadAllBytes(document.Export(folder.Path)));
        Assert.Equal(source, File.ReadAllBytes(path));
    }

    [Fact]
    public void EditsPreserveHeadersMetadataAndOtherHuesAndCanBeUndoneOrRestored()
    {
        using var folder = new TempFolder();
        byte[] source = Fixture();
        var document = HueDocument.Open(folder.Write(source));
        document.Put(1, "Red draft", RedRamp());
        Assert.True(document.HasChanges);
        Assert.Equal("Red draft", document.Read(1).Name);
        byte[] export = File.ReadAllBytes(document.Export(folder.Path));
        Assert.Equal(source[..4], export[..4]);
        Assert.Equal(source[68..72], export[68..72]); // tableStart/tableEnd remain untouched.
        Assert.Equal(source[92..], export[92..]); // All other source records remain byte-exact.
        document.Undo(1);
        Assert.False(document.HasChanges);
        document.Put(1, "Again", RedRamp());
        document.Restore(1);
        Assert.False(document.HasChanges);
        document.Undo(1);
        Assert.Equal("Again", document.Read(1).Name);
        Assert.Throws<ArgumentException>(() => document.Put(1, "A name longer than twenty characters", RedRamp()));
        Assert.Throws<ArgumentException>(() => document.Put(1, "Non ASCII é", RedRamp()));
        Assert.Throws<ArgumentException>(() => document.Put(1, "Bad", Enumerable.Repeat((ushort)0xFFFF, 32).ToArray()));
        Assert.Equal("Again", document.Read(1).Name);
    }

    [Fact]
    public void EditingPreservesUnusualUnchangedSourceWordsAndNameBytes()
    {
        using var folder = new TempFolder();
        byte[] source = Fixture();
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(4), 0xFFFF);
        source[72] = 10; // An existing non-printable name is preserved until explicitly renamed.
        var document = HueDocument.Open(folder.Write(source));
        var entry = document.Read(1);
        ushort[] colors = entry.Colors.ToArray();
        colors[31] = 0x7C00;
        document.Put(1, entry.Name, colors);
        byte[] bytes = File.ReadAllBytes(document.Export(folder.Path));
        Assert.Equal(source[4..6], bytes[4..6]);
        Assert.Equal(source[72..92], bytes[72..92]);
        Assert.Equal((ushort)0x7C00, HueDocument.Open(document.Export(folder.Path)).Read(1).Colors.Span[31]);
    }

    [Fact]
    public void NewHuesAppendWithoutRenumberingAndExportPadsOnlyTheLastBlock()
    {
        using var folder = new TempFolder();
        byte[] source = Fixture();
        string path = folder.Write(source);
        var document = HueDocument.Open(path);
        Assert.Equal(9, document.Add("New red", RedRamp()));
        Assert.Equal(10, document.Add("Another", new ushort[32]));
        string output = document.Export(folder.Path);
        byte[] bytes = File.ReadAllBytes(output);
        Assert.Equal(source, bytes[..708]);
        Assert.Equal(1416, bytes.Length);
        var reopened = HueDocument.Open(output);
        Assert.Equal(16, reopened.Count);
        Assert.Equal("New red", reopened.Read(9).Name);
        Assert.Equal(RedRamp(), reopened.Read(9).Colors.ToArray());
        Assert.Equal("Another", reopened.Read(10).Name);
        Assert.All(reopened.Read(16).Colors.ToArray(), color => Assert.Equal((ushort)0, color));
        Assert.Equal(source, File.ReadAllBytes(path));
        Assert.NotEqual(output, document.Export(folder.Path));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(707)] [InlineData(709)] [InlineData(1449984)]
    public void TruncatedEmptyAndOversizedLibrariesAreRejected(int length)
    {
        using var folder = new TempFolder();
        Assert.Throws<InvalidDataException>(() => HueDocument.Open(folder.Write(new byte[length])));
    }

    [Fact]
    public void DraftLibrariesCanStartEmptyAndUndoKeepsFiftyRecentChanges()
    {
        using var folder = new TempFolder();
        var document = HueDocument.Create();
        Assert.Throws<InvalidOperationException>(() => document.Export(folder.Path));
        int id = document.Add("New", new ushort[32]);
        Assert.Equal(1, id);
        for (int i = 1; i <= 60; i++) document.Put(id, "Edit " + i, RedRamp());
        for (int i = 0; i < 50; i++) document.Undo(id);
        Assert.Equal("Edit 10", document.Read(id).Name);
        Assert.False(document.CanUndo(id));
        Assert.Throws<InvalidOperationException>(() => document.Restore(id));
        Assert.Throws<ArgumentException>(() => document.Export("relative-folder"));
        Assert.Equal(8, HueDocument.Open(document.Export(folder.Path)).Count);
    }

    [Fact]
    public void UserBoardsAndDraftsAreIsolatedAndFailedOpenPreservesWork()
    {
        using var folder = new TempFolder();
        folder.Write(Fixture());
        var first = new ColorStudioWorkspace(); var second = new ColorStudioWorkspace();
        first.Open(folder.Path); second.Open(folder.Path);
        var image = DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFFFFFF)]);
        first.Add(AssetKind.Item, 123, image, HueMode.Full);
        first.Add(AssetKind.Item, 123, image, HueMode.GrayPixelsOnly);
        first.Document!.Put(1, "Draft", RedRamp());
        Assert.Equal(2, first.Samples.Count);
        Assert.Empty(second.Samples);
        Assert.Equal("First hue", second.Document!.Read(1).Name);
        Assert.Throws<InvalidOperationException>(() => first.Open(folder.Path));
        Assert.Throws<DirectoryNotFoundException>(() => first.Open(System.IO.Path.Combine(folder.Path, "missing"), true));
        Assert.Equal(2, first.Samples.Count);
        Assert.Equal("Draft", first.Document.Read(1).Name);
        first.Open(folder.Path, true);
        Assert.Empty(first.Samples);
        Assert.False(first.Document!.HasChanges);
        for (int i = 0; i < 24; i++) first.Add(AssetKind.Item, 123, image, HueMode.Full);
        Assert.Throws<InvalidOperationException>(() => first.Add(AssetKind.Gump, 123, image, HueMode.Full));
    }

    private static byte[] Fixture()
    {
        byte[] bytes = new byte[708];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEF);
        for (int i = 0; i < 8; i++)
        {
            var entry = bytes.AsSpan(4 + i * 88, 88);
            for (int j = 0; j < 32; j++) BinaryPrimitives.WriteUInt16LittleEndian(entry[(j * 2)..], (ushort)(i * 32 + j));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], 0x1234);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[66..], 0x4321);
            System.Text.Encoding.ASCII.GetBytes(i == 0 ? "First hue" : "Other hue", entry[68..]);
            entry[87] = 0xCA; // Preserve even bytes following the name's null terminator.
        }
        return bytes;
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fiddler-hues-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public string Write(byte[] bytes) { string path = System.IO.Path.Combine(Path, "hues.mul"); File.WriteAllBytes(path, bytes); return path; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
