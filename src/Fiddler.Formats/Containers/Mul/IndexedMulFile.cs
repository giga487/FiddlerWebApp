using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Fiddler.Formats.Containers.Mul;

/// <summary>Read-only IDX/MUL storage. Knows offsets and lengths, never resource types or pixels.</summary>
public sealed class IndexedMulFile : IDisposable
{
    private const int MaxIndexBytes = 12 * 1_000_000;
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private readonly SafeFileHandle handle;
    private readonly MulIndexEntry?[] slots;
    public IReadOnlyList<MulIndexEntry> Entries { get; }
    public int SlotCount => slots.Length;
    public bool Contains(int slot) => slot >= 0 && slot < slots.Length && slots[slot] is not null;
    public string DataFileName { get; }

    private IndexedMulFile(SafeFileHandle handle, MulIndexEntry?[] slots, string fileName)
    {
        this.handle = handle;
        this.slots = slots;
        Entries = Array.AsReadOnly(slots.OfType<MulIndexEntry>().ToArray());
        DataFileName = fileName;
    }

    public static IndexedMulFile Open(string indexPath, string dataPath)
    {
        using var index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (index.Length == 0 || index.Length % 12 != 0 || index.Length > MaxIndexBytes)
            throw new InvalidDataException("Indice MUL non valido: attesi record di 12 byte.");
        var bytes = new byte[(int)index.Length];
        index.ReadExactly(bytes);
        var entries = new MulIndexEntry?[bytes.Length / 12];
        for (int slot = 0; slot < entries.Length; slot++)
        {
            var record = bytes.AsSpan(slot * 12, 12);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(record);
            int length = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            int extra = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            if (offset == -1 || length == -1 || length == 0) continue;
            entries[slot] = new MulIndexEntry(slot, offset, length, extra);
        }
        var handle = File.OpenHandle(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try { return new IndexedMulFile(handle, entries, Path.GetFileName(dataPath)); }
        catch { handle.Dispose(); throw; }
    }

    public MulIndexEntry GetEntry(int slot)
    {
        if (slot < 0 || slot >= slots.Length || slots[slot] is not { } entry)
            throw new KeyNotFoundException("Slot non presente nell'indice.");
        return entry;
    }

    public byte[] Read(int slot)
    {
        var entry = GetEntry(slot);
        long fileLength = RandomAccess.GetLength(handle);
        if (entry.Offset < 0 || entry.Length <= 0 || entry.Length > MaxRecordBytes ||
            entry.Offset > fileLength - entry.Length)
            throw new InvalidDataException("Record IDX fuori dal MUL o superiore ai limiti.");
        var data = new byte[entry.Length];
        int read = 0;
        while (read < data.Length)
        {
            int count = RandomAccess.Read(handle, data.AsSpan(read), (long)entry.Offset + read);
            if (count == 0) throw new InvalidDataException("Il file MUL è stato troncato durante la lettura.");
            read += count;
        }
        return data;
    }

    public void Dispose() => handle.Dispose();
}
