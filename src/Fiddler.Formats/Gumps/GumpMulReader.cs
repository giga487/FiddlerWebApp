using Fiddler.Formats.Containers.Mul;

namespace Fiddler.Formats.Gumps;

/// <summary>Gump IDs, packed dimensions and RLE for gumpidx.mul + gumpart.mul.</summary>
public sealed class GumpMulReader : IGumpArchive
{
    private readonly IndexedMulFile file;
    public IReadOnlyList<MulEntry> Entries { get; }
    public string DataFileName => file.DataFileName;
    public int SlotCount => file.SlotCount;

    private GumpMulReader(IndexedMulFile file)
    {
        this.file = file;
        Entries = Array.AsReadOnly(file.Entries.Where(entry => entry.Extra != -1 && ((uint)entry.Extra >> 16) > 0 && (entry.Extra & 0xFFFF) > 0).Select(entry => new MulEntry(
            AssetKind.Gump, entry.Slot, entry.Offset, entry.Length, entry.Extra)).ToArray());
    }

    public static GumpMulReader Open(string indexPath, string dataPath)
    {
        var file = IndexedMulFile.Open(indexPath, dataPath);
        try { return new GumpMulReader(file); }
        catch { file.Dispose(); throw; }
    }

    public GumpRecord ReadRecord(int id)
    {
        var entry = file.GetEntry(id);
        return new GumpRecord(file.Read(id), (int)((uint)entry.Extra >> 16), entry.Extra & 0xFFFF);
    }
    public DecodedImage Read(int id) => ReadRecord(id).Decode();

    public void Dispose() => file.Dispose();
}
