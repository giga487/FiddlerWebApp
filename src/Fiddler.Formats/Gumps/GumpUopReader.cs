using System.Buffers.Binary;
using Fiddler.Formats.Containers.Uop;

namespace Fiddler.Formats.Gumps;

public sealed class GumpUopReader : IGumpArchive
{
    public const int MaxSlots = 0x12000;
    private static readonly Lazy<IReadOnlyDictionary<ulong, int>> Names = new(() => Enumerable.Range(0, MaxSlots)
        .ToDictionary(id => UopNameHash.HashFileName($"build/gumpartlegacymul/{id:D8}.tga"), id => id));
    private readonly UopArchive archive;
    private readonly Dictionary<int, ulong> ids = [];
    public IReadOnlyList<MulEntry> Entries { get; }
    public int SlotCount => ids.Count == 0 ? 0 : ids.Keys.Max() + 1;

    private GumpUopReader(UopArchive archive)
    {
        this.archive = archive;
        foreach (var entry in archive.Entries)
        {
            if (!Names.Value.TryGetValue(entry.Hash, out int id)) continue;
            if (entry.Compression == 0 && entry.StoredLength >= 8)
            {
                var prefix = archive.ReadStoredPrefix(entry.Hash, 8);
                if (BinaryPrimitives.ReadInt32LittleEndian(prefix) <= 0 || BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4)) <= 0) continue;
            }
            else if (entry.Compression == 1 && entry.ExpandedLength <= 8) continue;
            ids.Add(id, entry.Hash);
        }
        if (ids.Count == 0) throw new InvalidDataException("The UOP contains no recognized Gumps.");
        Entries = Array.AsReadOnly(ids.OrderBy(pair => pair.Key).Select(pair =>
        {
            var entry = archive.GetEntry(pair.Value);
            return new MulEntry(AssetKind.Gump, pair.Key, entry.Offset <= int.MaxValue ? (int)entry.Offset : -1, entry.StoredLength, 0);
        }).ToArray());
    }

    public static GumpUopReader Open(string path)
    {
        var archive = UopArchive.Open(path);
        try { return new GumpUopReader(archive); }
        catch { archive.Dispose(); throw; }
    }
    public GumpRecord ReadRecord(int id)
    {
        if (!ids.TryGetValue(id, out ulong hash)) throw new KeyNotFoundException("Gump ID not found in the UOP.");
        var data = archive.Read(hash);
        if (data.Length < 8) throw new InvalidDataException("Truncated Gump UOP payload.");
        return new GumpRecord(data[8..], BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)));
    }
    public DecodedImage Read(int id) => ReadRecord(id).Decode();
    public void Dispose() => archive.Dispose();
}
