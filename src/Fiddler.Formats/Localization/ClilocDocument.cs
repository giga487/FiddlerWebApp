using System.Buffers.Binary;
using System.Text;
using Fiddler.Formats.Containers.Uop;
using Fiddler.Formats.Editing;

namespace Fiddler.Formats.Localization;

public sealed record ClilocEntry(int Id, byte Flag, string Text);

public sealed class ClilocDocument
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] header;
    private readonly Dictionary<int, ClilocEntry> original;
    private readonly Dictionary<int, ClilocEntry?> edits = [];
    public string FileName { get; }
    public bool WasCompressed { get; }
    public int DirtyCount => edits.Count;
    public IEnumerable<ClilocEntry> Entries => original.Keys.Concat(edits.Keys).Distinct().Order()
        .Select(id => edits.TryGetValue(id, out var edit) ? edit : original[id]).OfType<ClilocEntry>();

    public static ClilocDocument Open(string path)
    {
        byte[] bytes = ResourceExport.Read(path, 64 * 1024 * 1024);
        try { return new(path, bytes, false); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            try { return new(path, MythicDecoder.Decode(bytes, 64 * 1024 * 1024), true); }
            catch (Exception decompression) when (decompression is InvalidDataException or DecoderFallbackException)
            { throw new InvalidDataException("Cannot parse Cliloc as plain or Mythic-compressed data.", decompression); }
        }
    }
    private ClilocDocument(string path, byte[] data, bool compressed)
    {
        FileName = Path.GetFileName(path); WasCompressed = compressed;
        if (data.Length < 6) throw new InvalidDataException("Truncated Cliloc header.");
        header = data[..6]; original = [];
        int position = 6;
        while (position < data.Length)
        {
            if (position > data.Length - 7) throw new InvalidDataException("Truncated Cliloc entry.");
            int id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
            byte flag = data[position + 4];
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 5));
            position += 7;
            if (id < 0 || position > data.Length - length || original.Count >= 1_000_000)
                throw new InvalidDataException("Invalid Cliloc ID, length or entry count.");
            if (!original.TryAdd(id, new(id, flag, Utf8.GetString(data, position, length))))
                throw new InvalidDataException("Duplicate Cliloc ID.");
            position += length;
        }
    }
    public void Put(ClilocEntry entry)
    {
        if (entry.Id < 0 || Utf8.GetByteCount(entry.Text) > ushort.MaxValue) throw new ArgumentException("Cliloc text must fit 65,535 UTF-8 bytes and use a nonnegative ID.");
        if (!edits.ContainsKey(entry.Id) && edits.Count >= 10000) throw new InvalidOperationException("Export or discard drafts before editing more entries.");
        if (edits.Where(p => p.Key != entry.Id && p.Value is not null).Sum(p => (long)Utf8.GetByteCount(p.Value!.Text)) + Utf8.GetByteCount(entry.Text) > 64 * 1024 * 1024)
            throw new InvalidOperationException("Cliloc drafts exceed 64 MiB. Export or discard them first.");
        edits[entry.Id] = entry;
    }
    public void Remove(int id) { if (!original.ContainsKey(id) && !edits.ContainsKey(id)) throw new KeyNotFoundException("Cliloc ID not found."); edits[id] = null; }
    public void Undo(int id) => edits.Remove(id);
    public void Discard() => edits.Clear();
    public string Export(string parent) => ResourceExport.Write(parent, "cliloc", folder =>
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(folder, FileName)));
        writer.Write(header);
        foreach (var entry in original.Keys.Concat(edits.Keys).Distinct().Select(id => edits.TryGetValue(id, out var edit) ? edit : original[id]).OfType<ClilocEntry>())
        {
            byte[] text = Utf8.GetBytes(entry.Text);
            writer.Write(entry.Id); writer.Write(entry.Flag); writer.Write((ushort)text.Length); writer.Write(text);
        }
    });
}
