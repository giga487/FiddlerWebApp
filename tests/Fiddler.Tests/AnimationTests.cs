using System.Buffers.Binary;
using Fiddler.Formats.Animations;
using Fiddler.Graphics.Animations;
using FiddlerWebApp.Services;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class AnimationTests
{
    // Synthetic records with deliberately different frame dimensions and signed pivots.
    public static byte[] Fixture()
    {
        static byte[] Frame(short x, short y, ushort width, ushort height, params (int X, int Y, byte[] Pixels)[] runs)
        {
            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory);
            writer.Write(x); writer.Write(y); writer.Write(width); writer.Write(height);
            foreach (var run in runs)
            {
                uint header = ((uint)run.X & 0x3FF) << 22 | ((uint)run.Y & 0x3FF) << 12 | (uint)run.Pixels.Length;
                writer.Write(header); writer.Write(run.Pixels);
            }
            writer.Write(0x7FFF7FFF);
            return memory.ToArray();
        }
        byte[] first = Frame(1, 0, 3, 2, (-1, -2, [1, 2, 3]), (0, -1, [4, 1]));
        byte[] second = Frame(2, 1, 4, 3, (-2, -4, [2, 1]), (-1, -2, [3, 1]));
        using var stream = new MemoryStream();
        using var bin = new BinaryWriter(stream);
        for (int i = 0; i < 256; i++)
            bin.Write((ushort)(i switch { 1 => 0x7C00, 2 => 0x03E0, 3 => 0x001F, 4 => 0x8000, _ => 0 }));
        bin.Write(2); bin.Write(12); bin.Write(12 + first.Length);
        bin.Write(first); bin.Write(second);
        return stream.ToArray();
    }

    [Fact]
    public void PaletteRleAndPivotMatchFiddlerFrameFormat()
    {
        var clip = AnimationDecoder.Decode(Fixture());
        Assert.Equal(2, clip.Frames.Count);
        var frame = clip.Frames[0];
        Assert.Equal((1, 0), (frame.CenterX, frame.CenterY));
        Assert.Equal((3, 2), (frame.Image!.Width, frame.Image.Height));
        Assert.Equal(new[] { unchecked((int)0xFFFF0000), unchecked((int)0xFF00FF00), unchecked((int)0xFF0000FF),
            0, 0, unchecked((int)0xFFFF0000) }, frame.Image.Pixels.ToArray());
    }

    [Fact]
    public void NegativeFrameCentersArePreservedAndUsedForDrawing()
    {
        var record = new byte[537];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 0x7C00);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(512), 1);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(516), 8);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(520), -1);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(522), -2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(524), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(526), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(528), (1U << 22) | 1);
        record[532] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(533), 0x7FFF7FFF);
        var clip = AnimationDecoder.Decode(record);
        Assert.Equal((-1, -2), (clip.Frames[0].CenterX, clip.Frames[0].CenterY));
        var renderer = new AnimationRenderer();
        Assert.Equal(new AnimationBounds(1, 0, 2, 2), renderer.GetBounds(clip));
        using var png = SKBitmap.Decode(renderer.RenderAtlas(clip).Png);
        Assert.Equal(SKColors.Red, png.GetPixel(0, 0));
        Assert.Equal(3, AnimationDecoder.Decode(record, true).Frames[0].CenterX);
    }

    [Fact]
    public void MirroringReversesPixelsAndUpdatesPivot()
    {
        var frame = AnimationDecoder.Decode(Fixture(), mirror: true).Frames[0];
        Assert.Equal(2, frame.CenterX);
        Assert.Equal(unchecked((int)0xFF0000FF), frame.Image!.Pixels.Span[0]);
        Assert.Equal(unchecked((int)0xFFFF0000), frame.Image.Pixels.Span[2]);
        Assert.Equal(unchecked((int)0xFFFF0000), frame.Image.Pixels.Span[3]);
        Assert.Equal(0, frame.Image.Pixels.Span[5]);
    }

    [Theory]
    [InlineData(1, 199, 21890, 22)]
    [InlineData(1, 200, 22000, 13)]
    [InlineData(1, 400, 35000, 35)]
    [InlineData(2, 400, 35000, 13)]
    [InlineData(3, 299, 19435, 13)]
    [InlineData(3, 300, 33000, 22)]
    [InlineData(4, 200, 22000, 13)]
    [InlineData(5, 34, 11210, 13)]
    [InlineData(6, 200, 22000, 13)]
    public void PhysicalLayoutMatchesFiddlerAndActionsDoNotSpillToNextBody(int file, int body, int start, int capacity)
    {
        Assert.Equal(start, AnimationIndexLayout.Slot(body, 0, 0, file));
        Assert.Equal(capacity, AnimationIndexLayout.ActionCapacity(body, file));
        Assert.Equal(start + 5 + 3, AnimationIndexLayout.Slot(body, 1, 5, file));
        Assert.Equal(start + 5 + 2, AnimationIndexLayout.Slot(body, 1, 6, file));
        Assert.Equal(start + 5 + 1, AnimationIndexLayout.Slot(body, 1, 7, file));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationIndexLayout.Slot(body, capacity, 0, file));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationIndexLayout.Slot(body, 0, 8, file));
    }

    [Fact]
    public void CorruptFrameTablesRunsAndTruncationFailWithFormatErrors()
    {
        byte[] original = Fixture();
        for (int length = 0; length < original.Length; length++)
        {
            byte[] truncated = original[..length];
            Assert.Throws<InvalidDataException>(() => AnimationDecoder.Decode(truncated));
        }
        byte[] record = Fixture();
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(512), 513);
        Assert.Throws<InvalidDataException>(() => AnimationDecoder.Decode(record));
        record = Fixture();
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(516), 0);
        Assert.Throws<InvalidDataException>(() => AnimationDecoder.Decode(record));
        record = Fixture();
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(520), 13); // overlapping frame header
        Assert.Throws<InvalidDataException>(() => AnimationDecoder.Decode(record));
        record = Fixture();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(528), 65535); // width of first frame
        Assert.Throws<InvalidDataException>(() => AnimationDecoder.Decode(record));
        record = Fixture();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(532), 0x00000FFF); // oversized run
        Assert.Throws<InvalidDataException>(() => AnimationDecoder.Decode(record));
    }

    [Fact]
    public void AtlasUsesOneSharedAnchorAcrossDifferentFrameSizes()
    {
        var clip = AnimationDecoder.Decode(Fixture());
        var renderer = new AnimationRenderer();
        Assert.Equal(new AnimationBounds(-2, -4, 4, 4), renderer.GetBounds(clip));
        var atlas = renderer.RenderAtlas(clip);
        Assert.Equal((4, 4, 2, 2), (atlas.FrameWidth, atlas.FrameHeight, atlas.Columns, atlas.FrameCount));
        using var pixels = SKBitmap.Decode(atlas.Png);
        Assert.Equal(SKColors.Red, pixels.GetPixel(1, 2)); // first frame at (+1,+2)
        Assert.Equal(SKColors.Lime, pixels.GetPixel(4, 0)); // second frame at (+0,+0)
        Assert.Equal((byte)0, pixels.GetPixel(0, 0).Alpha);
        using var mirrored = SKBitmap.Decode(renderer.RenderAtlas(AnimationDecoder.Decode(Fixture(), true)).Png);
        Assert.Equal(SKColors.Blue, mirrored.GetPixel(0, 2));
    }

    [Fact]
    public void PlaybackSupportsPauseSeekVariableDelayLoopAndDrawingOnSkiaCanvas()
    {
        var player = new AnimationPlayer(AnimationDecoder.Decode(Fixture()));
        Assert.Equal(TimeSpan.FromMilliseconds(150), player.FrameDelay);
        player.Play();
        player.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(0, player.FrameIndex);
        player.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, player.FrameIndex);
        player.Pause(); player.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, player.FrameIndex);
        player.Seek(0); player.FrameDelay = TimeSpan.FromMilliseconds(50); player.Play();
        player.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, player.FrameIndex);
        player.Loop = false; player.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, player.FrameIndex);
        Assert.False(player.IsPlaying);
        using var bitmap = new SKBitmap(10, 10);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        player.Draw(canvas, 4, 5);
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Seek(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => player.FrameDelay = TimeSpan.Zero);
    }

    [Fact]
    public void ReaderDiscoversAnimalsActionsAndMirroredDirectionsFromSparseIndex()
    {
        using var folder = new TestFolder();
        var record = Fixture();
        byte[] index = Enumerable.Repeat((byte)255, (22000 + 15) * 12).ToArray();
        foreach (int slot in new[] { 22001, 22003, 22011 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(slot * 12), 0);
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(slot * 12 + 4), record.Length);
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(slot * 12 + 8), 0);
        }
        File.WriteAllBytes(Path.Combine(folder.Path, "anim.idx"), index);
        File.WriteAllBytes(Path.Combine(folder.Path, "anim.mul"), record);
        using var reader = AnimationMulReader.Open(Path.Combine(folder.Path, "anim.idx"), Path.Combine(folder.Path, "anim.mul"));
        Assert.Equal(new AnimationBody(200, AnimationBodyType.Animal), Assert.Single(reader.Bodies));
        Assert.Equal(new[] { new AnimationAction(0, "Walk"), new AnimationAction(2, "Idle") }, reader.Actions(200));
        Assert.True(reader.Read(200, 0, 5).Mirrored);
        Assert.False(reader.HasSequence(200, 0, 0));
        Assert.Throws<KeyNotFoundException>(() => reader.Read(200, 0, 0));
        using var workspace = new AnimationWorkspace();
        workspace.Open(folder.Path);
        Assert.Single(workspace.Readers);
        Assert.Throws<DirectoryNotFoundException>(() => workspace.Open(Path.Combine(folder.Path, "missing")));
        Assert.Single(workspace.Readers);
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("fiddler-animation-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
