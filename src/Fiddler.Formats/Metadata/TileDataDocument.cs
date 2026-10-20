using System.Buffers.Binary;
using System.Text;
using Fiddler.Formats.Editing;

namespace Fiddler.Formats.Metadata;

public enum TileDataLayout { Auto, Legacy32, Extended64 }
public sealed record TileMetadata(AssetKind Kind, int Id, string Name, ulong Flags, ushort TextureId = 0,
    byte Weight = 0, byte Quality = 0, short MiscData = 0, byte Unknown2 = 0, byte Quantity = 0,
    ushort Animation = 0, byte Unknown3 = 0, byte Hue = 0, byte StackingOffset = 0, byte Value = 0, byte Height = 0)
{
    public bool PartialHue => (Flags & 0x40000) != 0;
}

public sealed class TileDataDocument
{
    private readonly byte[] source;
    private readonly Dictionary<(AssetKind Kind, int Id), TileMetadata> edits = [];
    public TileDataLayout Layout { get; }
    public int ItemCount { get; }
    public int DirtyCount => edits.Count;
    private int FlagBytes => Layout == TileDataLayout.Extended64 ? 8 : 4;
    private int LandSize => FlagBytes + 22;
    private int ItemSize => FlagBytes + 33;
    private int LandBytes => 512 * (4 + 32 * LandSize);
    public static bool Fits(long length, TileDataLayout layout)
    {
        int flags = layout == TileDataLayout.Extended64 ? 8 : 4;
        long rest = length - 512 * (4 + 32 * (flags + 22));
        return rest >= 0 && rest % (4 + 32 * (flags + 33)) == 0 && rest / (4 + 32 * (flags + 33)) <= 2048;
    }
    public static TileDataDocument Open(string path, TileDataLayout layout = TileDataLayout.Auto)
    {
        byte[] source = ResourceExport.Read(path, 8 * 1024 * 1024);
        if (layout == TileDataLayout.Auto)
        {
            bool legacy = Fits(source.Length, TileDataLayout.Legacy32), extended = Fits(source.Length, TileDataLayout.Extended64);
            if (legacy == extended) throw new InvalidDataException("TileData layout is invalid or ambiguous. Select Legacy32 or Extended64 explicitly.");
            layout = legacy ? TileDataLayout.Legacy32 : TileDataLayout.Extended64;
        }
        if (layout is not (TileDataLayout.Legacy32 or TileDataLayout.Extended64) || !Fits(source.Length, layout)) throw new InvalidDataException("TileData length does not match the selected layout.");
        return new(source, layout);
    }
    private TileDataDocument(byte[] source, TileDataLayout layout)
    { this.source = source; Layout = layout; ItemCount = (source.Length - LandBytes) / (4 + ItemSize * 32) * 32; }
    private int Offset(AssetKind kind, int id)
    {
        if (kind is not (AssetKind.Item or AssetKind.Land) || id < 0 || id >= (kind == AssetKind.Land ? 0x4000 : ItemCount))
            throw new ArgumentOutOfRangeException(nameof(id), "TileData ID is outside the selected table.");
        int size = kind == AssetKind.Land ? LandSize : ItemSize;
        return (kind == AssetKind.Land ? 0 : LandBytes) + id / 32 * (4 + size * 32) + 4 + id % 32 * size;
    }
    public TileMetadata Read(AssetKind kind, int id)
    {
        if (edits.TryGetValue((kind, id), out var edited)) return edited;
        var record = source.AsSpan(Offset(kind, id), kind == AssetKind.Land ? LandSize : ItemSize);
        ulong flags = FlagBytes == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(record) : BinaryPrimitives.ReadUInt32LittleEndian(record);
        var fields = record[FlagBytes..];
        string name = Encoding.ASCII.GetString(fields[^20..]).TrimEnd('\0');
        return kind == AssetKind.Land ? new(kind, id, name, flags, BinaryPrimitives.ReadUInt16LittleEndian(fields)) :
            new(kind, id, name, flags, Weight: fields[0], Quality: fields[1], MiscData: BinaryPrimitives.ReadInt16LittleEndian(fields[2..]),
                Unknown2: fields[4], Quantity: fields[5], Animation: BinaryPrimitives.ReadUInt16LittleEndian(fields[6..]),
                Unknown3: fields[8], Hue: fields[9], StackingOffset: fields[10], Value: fields[11], Height: fields[12]);
    }
    public void Put(TileMetadata entry)
    {
        _ = Offset(entry.Kind, entry.Id);
        if (entry.Name.Length > 20 || entry.Name.Any(c => c is < ' ' or > '~') || FlagBytes == 4 && entry.Flags > uint.MaxValue)
            throw new ArgumentException("Tile name must use at most 20 printable ASCII characters; flags must fit the selected layout.");
        edits[(entry.Kind, entry.Id)] = entry;
    }
    public void Undo(AssetKind kind, int id) => edits.Remove((kind, id));
    public void Discard() => edits.Clear();
    public string Export(string parent) => ResourceExport.Write(parent, "tiledata", folder =>
    {
        byte[] data = source.ToArray();
        foreach (var entry in edits.Values)
        {
            var record = data.AsSpan(Offset(entry.Kind, entry.Id), entry.Kind == AssetKind.Land ? LandSize : ItemSize);
            if (FlagBytes == 8) BinaryPrimitives.WriteUInt64LittleEndian(record, entry.Flags);
            else BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)entry.Flags);
            var fields = record[FlagBytes..];
            if (entry.Kind == AssetKind.Land) BinaryPrimitives.WriteUInt16LittleEndian(fields, entry.TextureId);
            else
            {
                fields[0] = entry.Weight; fields[1] = entry.Quality; BinaryPrimitives.WriteInt16LittleEndian(fields[2..], entry.MiscData);
                fields[4] = entry.Unknown2; fields[5] = entry.Quantity; BinaryPrimitives.WriteUInt16LittleEndian(fields[6..], entry.Animation);
                fields[8] = entry.Unknown3; fields[9] = entry.Hue; fields[10] = entry.StackingOffset; fields[11] = entry.Value; fields[12] = entry.Height;
            }
            fields[^20..].Clear(); Encoding.ASCII.GetBytes(entry.Name, fields[^20..]);
        }
        File.WriteAllBytes(Path.Combine(folder, "tiledata.mul"), data);
    });
}
