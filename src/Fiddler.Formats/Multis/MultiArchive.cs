using System.Buffers.Binary;
using Fiddler.Formats.Containers.Mul;
using Fiddler.Formats.Containers.Uop;

namespace Fiddler.Formats.Multis;

public sealed record MultiComponent(int Index, int ItemId, short X, short Y, short Z, int Flags, int Extra);

/// <summary>Lazy multi reader. MUL rows are explicitly legacy (12 bytes) or High Seas (16 bytes).</summary>
public sealed class MultiArchive : IDisposable
{
    private readonly IndexedMulFile? mul;
    private readonly UopArchive? uop;
    private readonly Dictionary<int, ulong> hashes = [];
    private readonly int rowSize;
    public IReadOnlyList<int> Ids { get; }
    public string Format => uop is not null ? "MultiCollection UOP" : $"MUL / IDX · {rowSize} bytes";
    private MultiArchive(IndexedMulFile file, int rowSize)
    { mul = file; this.rowSize = rowSize; Ids = Array.AsReadOnly(file.Entries.Select(e => e.Slot).Order().ToArray()); }
    private MultiArchive(UopArchive file)
    {
        uop = file;
        var entries = file.Entries.ToDictionary(e => e.Hash);
        for (int id = 0; id < 0x10000; id++)
        {
            ulong hash = UopNameHash.HashFileName($"build/multicollection/{id:D6}.bin");
            if (entries.ContainsKey(hash)) hashes.Add(id, hash);
        }
        Ids = Array.AsReadOnly(hashes.Keys.Order().ToArray());
        if (Ids.Count == 0) throw new InvalidDataException("No recognized Multis in the UOP.");
    }
    public static MultiArchive Open(string dataPath, string? indexPath = null, int rowSize = 16)
    {
        if (Path.GetExtension(dataPath).Equals(".uop", StringComparison.OrdinalIgnoreCase))
        {
            var file = UopArchive.Open(dataPath);
            try { return new MultiArchive(file); } catch { file.Dispose(); throw; }
        }
        if (rowSize is not (12 or 16)) throw new ArgumentException("Multi format must be 12 or 16 bytes.");
        var mul = IndexedMulFile.Open(indexPath ?? throw new ArgumentException("multi.idx is required."), dataPath);
        try { return new MultiArchive(mul, rowSize); } catch { mul.Dispose(); throw; }
    }
    public IReadOnlyList<MultiComponent> Read(int id)
    {
        byte[] data;
        if (uop is null)
        {
            data = mul!.Read(id);
            if (data.Length % rowSize != 0 || data.Length / rowSize > 50_000)
                throw new InvalidDataException("Invalid Multi record: check the 12/16-byte format (maximum 50,000 components).");
            var result = new List<MultiComponent>(data.Length / rowSize);
            for (int p = 0; p < data.Length; p += rowSize)
                result.Add(new(result.Count, U16(data, p), I16(data, p + 2), I16(data, p + 4), I16(data, p + 6), I32(data, p + 8), rowSize == 16 ? I32(data, p + 12) : 0));
            return result.AsReadOnly();
        }
        if (!hashes.TryGetValue(id, out ulong hash)) throw new KeyNotFoundException("Multi ID not found.");
        data = uop.Read(hash);
        if (data.Length < 8 || I32(data, 0) != id) throw new InvalidDataException("Invalid Multi UOP header.");
        int count = I32(data, 4), position = 8;
        if (count < 0 || count > 50_000 || count > (data.Length - 8) / 14) throw new InvalidDataException("Invalid Multi component count.");
        var components = new List<MultiComponent>(count);
        for (int i = 0; i < count; i++)
        {
            if (position > data.Length - 14) throw new InvalidDataException("Truncated Multi UOP component.");
            int flags = U16(data, position + 8), clilocs = I32(data, position + 10);
            if (clilocs < 0 || clilocs > (data.Length - position - 14) / 4) throw new InvalidDataException("Multi UOP cliloc data extends beyond the record.");
            // Visibility and extra bit mapping follow UOFiddler/Ultima/Multis.cs.
            components.Add(new(i, U16(data, position), I16(data, position + 2), I16(data, position + 4), I16(data, position + 6), (flags & 1) == 0 ? 1 : 0, (flags & 0x100) != 0 ? 1 : 0));
            position += 14 + clilocs * 4;
        }
        if (position != data.Length) throw new InvalidDataException("Unexpected data after the Multi UOP components.");
        return components.AsReadOnly();
    }
    private static ushort U16(byte[] bytes, int p) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p));
    private static short I16(byte[] bytes, int p) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(p));
    private static int I32(byte[] bytes, int p) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p));
    public void Dispose() { mul?.Dispose(); uop?.Dispose(); }
}
