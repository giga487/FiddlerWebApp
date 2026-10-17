using Fiddler.Formats.Containers.Mul;

namespace Fiddler.Formats.Art;

/// <summary>Land and static art ID mapping and decoding for artidx.mul + art.mul.</summary>
public sealed class ArtMulReader : IDisposable
{
    private const int ItemSlotOffset = 0x4000;
    private readonly IndexedMulFile file;
    public IReadOnlyList<MulEntry> LandEntries { get; }
    public IReadOnlyList<MulEntry> ItemEntries { get; }
    public string DataFileName => file.DataFileName;

    private ArtMulReader(IndexedMulFile file)
    {
        this.file = file;
        var entries = file.Entries.Select(entry => new MulEntry(
            entry.Slot < ItemSlotOffset ? AssetKind.Land : AssetKind.Item,
            entry.Slot < ItemSlotOffset ? entry.Slot : entry.Slot - ItemSlotOffset,
            entry.Offset, entry.Length, entry.Extra)).ToArray();
        LandEntries = Array.AsReadOnly(entries.Where(entry => entry.Kind == AssetKind.Land).ToArray());
        ItemEntries = Array.AsReadOnly(entries.Where(entry => entry.Kind == AssetKind.Item).ToArray());
    }

    public static ArtMulReader Open(string indexPath, string dataPath)
    {
        var file = IndexedMulFile.Open(indexPath, dataPath);
        try { return new ArtMulReader(file); }
        catch { file.Dispose(); throw; }
    }

    public DecodedImage ReadLand(int id)
    {
        if (id < 0 || id >= ItemSlotOffset) throw new KeyNotFoundException("ID terreno non valido.");
        return LandDecoder.Decode(file.Read(id));
    }

    public DecodedImage ReadItem(int id)
    {
        if (id < 0 || id > int.MaxValue - ItemSlotOffset)
            throw new KeyNotFoundException("ID oggetto non valido.");
        return StaticArtDecoder.Decode(file.Read(id + ItemSlotOffset));
    }

    public void Dispose() => file.Dispose();
}
