using Fiddler.Formats.Containers.Mul;
using Fiddler.Formats.Editing;

namespace Fiddler.Formats.Art;

public sealed class ArtDocument : IDisposable
{
    private readonly IndexedMulFile source;
    private readonly Dictionary<int, byte[]?> edits = [];
    public int DirtyCount => edits.Count;
    public bool HasChanges => DirtyCount > 0;
    public int SlotCount => Math.Max(source.SlotCount, edits.Keys.DefaultIfEmpty(-1).Max() + 1);
    public IReadOnlyDictionary<int, byte[]?> GetChanges() => edits.ToDictionary(pair => pair.Key, pair => pair.Value?.ToArray());
    private ArtDocument(IndexedMulFile source) => this.source = source;
    public static ArtDocument Open(string index, string data) => new(IndexedMulFile.Open(index, data));
    private static int Slot(AssetKind kind, int id)
    {
        if (kind is not (AssetKind.Item or AssetKind.Land) || id < 0 || id >= (kind == AssetKind.Land ? 0x4000 : 1_000_000 - 0x4000)) throw new ArgumentOutOfRangeException(nameof(id));
        return id + (kind == AssetKind.Item ? 0x4000 : 0);
    }
    public IReadOnlyList<MulEntry> Entries(AssetKind kind)
    {
        var entries = source.Entries.ToDictionary(e => e.Slot, e => new MulEntry(e.Slot < 0x4000 ? AssetKind.Land : AssetKind.Item, e.Slot < 0x4000 ? e.Slot : e.Slot - 0x4000, e.Offset, e.Length, e.Extra));
        foreach (var (slot, data) in edits)
        {
            if (data is null) entries.Remove(slot);
            else entries[slot] = new(slot < 0x4000 ? AssetKind.Land : AssetKind.Item, slot < 0x4000 ? slot : slot - 0x4000, -1, data.Length, 0);
        }
        return entries.Values.Where(e => e.Kind == kind).OrderBy(e => e.Id).ToArray();
    }
    public DecodedImage Read(AssetKind kind, int id)
    {
        int slot = Slot(kind, id);
        byte[] bytes = edits.TryGetValue(slot, out var edited) ? edited ?? throw new KeyNotFoundException("Art ID is removed in this draft.") : source.Read(slot);
        return kind == AssetKind.Land ? LandDecoder.Decode(bytes) : StaticArtDecoder.Decode(bytes);
    }
    public void Put(AssetKind kind, int id, DecodedImage image)
    {
        int slot = Slot(kind, id); byte[] data = kind == AssetKind.Land ? ArtEncoder.Land(image) : ArtEncoder.Item(image);
        if (edits.Count >= 512 && !edits.ContainsKey(slot) || edits.Where(p => p.Key != slot).Sum(p => (long)(p.Value?.Length ?? 0)) + data.Length > 64 * 1024 * 1024)
            throw new InvalidOperationException("Export or discard Art drafts before adding more changes.");
        edits[slot] = data;
    }
    public void Remove(AssetKind kind, int id)
    {
        int slot = Slot(kind, id);
        if (!edits.ContainsKey(slot) && edits.Count >= 512) throw new InvalidOperationException("Export or discard Art drafts before adding more changes.");
        edits[slot] = null;
    }
    public void Undo(AssetKind kind, int id) => edits.Remove(Slot(kind, id));
    public bool IsModified(AssetKind kind, int id) => edits.ContainsKey(Slot(kind, id));
    public void Discard() => edits.Clear();
    public string Export(string parent) => ResourceExport.Write(parent, "art", folder =>
    {
        using var data = new BinaryWriter(File.Create(Path.Combine(folder, "art.mul")));
        using var index = new BinaryWriter(File.Create(Path.Combine(folder, "artidx.mul")));
        int count = Math.Max(source.SlotCount, Math.Max(0x4000, edits.Keys.DefaultIfEmpty(-1).Max() + 1));
        for (int slot = 0; slot < count; slot++)
        {
            bool changed = edits.TryGetValue(slot, out var bytes);
            if (!changed && source.Contains(slot)) bytes = source.Read(slot);
            if (bytes is null) { index.Write(-1); index.Write(0); index.Write(0); continue; }
            if (data.BaseStream.Position > int.MaxValue - bytes.Length) throw new InvalidDataException("Art MUL exceeds the 32-bit offset limit.");
            index.Write((int)data.BaseStream.Position); index.Write(bytes.Length); index.Write(!changed ? source.GetEntry(slot).Extra : 0); data.Write(bytes);
        }
    });
    public void Dispose() => source.Dispose();
}
