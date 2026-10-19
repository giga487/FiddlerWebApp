using System.Buffers.Binary;
using System.Text;

namespace Fiddler.Formats.Hues;

public sealed record HueEntry(int Id, string Name, ReadOnlyMemory<ushort> Colors);

/// <summary>Session drafts over the original hue bytes. IDs are one-based game IDs.</summary>
public sealed class HueDocument
{
    public const int MaxHues = 16376;
    private const int BlockSize = 708, EntrySize = 88;
    private readonly byte[] source;
    private readonly List<byte[]> records = [];
    private readonly Dictionary<int, Stack<byte[]>> history = [];
    public int Count => records.Count;
    public int SourceCount => source.Length / BlockSize * 8;
    public bool HasChanges => Count != SourceCount || Enumerable.Range(1, SourceCount).Any(IsModified);

    private HueDocument(byte[] source)
    {
        this.source = source;
        for (int i = 0; i < SourceCount; i++) records.Add(source.AsSpan(Offset(i), EntrySize).ToArray());
    }

    public static HueDocument Create() => new([]);

    public static HueDocument Open(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length % BlockSize != 0 || stream.Length > (long)MaxHues / 8 * BlockSize)
            throw new InvalidDataException("Invalid hues.mul length: expected complete 708-byte blocks within the game hue ID range.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return new HueDocument(bytes);
    }

    private static int Offset(int index) => index / 8 * BlockSize + 4 + index % 8 * EntrySize;
    private byte[] Record(int id) => id >= 1 && id <= Count ? records[id - 1] : throw new ArgumentOutOfRangeException(nameof(id), "Choose an existing game hue ID (1 or higher).");
    public HueEntry Read(int id)
    {
        byte[] record = Record(id);
        ushort[] colors = new ushort[32];
        for (int i = 0; i < 32; i++) colors[i] = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(i * 2));
        var name = record.AsSpan(68, 20);
        int end = name.IndexOf((byte)0);
        return new HueEntry(id, Encoding.ASCII.GetString(end < 0 ? name : name[..end]), colors);
    }

    public bool IsModified(int id)
    {
        var record = Record(id);
        return id > SourceCount || !record.AsSpan().SequenceEqual(source.AsSpan(Offset(id - 1), EntrySize));
    }

    public bool CanUndo(int id) => history.TryGetValue(id, out var stack) && stack.Count > 0;
    private void Remember(int id)
    {
        if (!history.TryGetValue(id, out var stack)) history[id] = stack = new();
        if (stack.Count == 50) history[id] = stack = new Stack<byte[]>(stack.Take(49).Reverse());
        stack.Push(Record(id).ToArray());
    }

    public void Put(int id, string name, ReadOnlySpan<ushort> colors)
    {
        byte[] record = Record(id);
        Validate(name, colors, Read(id).Name, record);
        byte[] next = record.ToArray();
        for (int i = 0; i < 32; i++) BinaryPrimitives.WriteUInt16LittleEndian(next.AsSpan(i * 2), colors[i]);
        if (Read(id).Name != name)
        {
            next.AsSpan(68, 20).Clear();
            Encoding.ASCII.GetBytes(name, next.AsSpan(68, 20));
        }
        if (record.AsSpan().SequenceEqual(next)) return;
        Remember(id);
        records[id - 1] = next;
    }

    private static void Validate(string name, ReadOnlySpan<ushort> colors, string? originalName = null, ReadOnlySpan<byte> original = default)
    {
        if (name != originalName && (name.Length > 20 || name.Any(c => c < 32 || c > 126)))
            throw new ArgumentException("The hue name must contain at most 20 printable ASCII characters.");
        if (colors.Length != 32) throw new ArgumentException("A hue must contain exactly 32 colors.");
        for (int i = 0; i < colors.Length; i++)
            if (colors[i] > 0x7FFF && (original.IsEmpty || colors[i] != BinaryPrimitives.ReadUInt16LittleEndian(original[(i * 2)..])))
                throw new ArgumentException("Palette colors must be valid 15-bit RGB values.");
    }

    public int Add(string name, ReadOnlySpan<ushort> colors)
    {
        Validate(name, colors);
        if (Count >= MaxHues) throw new InvalidOperationException("The game hue ID limit has been reached.");
        records.Add(new byte[EntrySize]);
        Put(Count, name, colors);
        history.Remove(Count);
        return Count;
    }

    public void Undo(int id)
    {
        _ = Record(id);
        if (CanUndo(id)) records[id - 1] = history[id].Pop();
    }

    public void Restore(int id)
    {
        if (id > SourceCount) throw new InvalidOperationException("A new hue has no original palette to restore.");
        if (!IsModified(id)) return;
        Remember(id);
        records[id - 1] = source.AsSpan(Offset(id - 1), EntrySize).ToArray();
    }

    public string Export(string parentDirectory)
    {
        if (Count == 0) throw new InvalidOperationException("Create a hue before exporting.");
        if (!Path.IsPathFullyQualified(parentDirectory)) throw new ArgumentException("Enter an absolute output folder path on the server.");
        // Each appended block needs eight records. Zero padding leaves all existing IDs unchanged.
        byte[] output = new byte[(Count + 7) / 8 * BlockSize];
        source.CopyTo(output, 0);
        for (int i = 0; i < Count; i++) records[i].CopyTo(output, Offset(i));
        string destination = Path.Combine(Path.GetFullPath(parentDirectory), "hues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        string path = Path.Combine(destination, "hues.mul");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(output);
            return path;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(destination);
            throw;
        }
    }
}
