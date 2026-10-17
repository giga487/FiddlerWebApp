using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Fiddler.Formats;

/// <summary>A read-only IDX/MUL pair. Own one instance per workspace and dispose it on close.</summary>
public sealed class MulArchive : IDisposable
{
    private const int MaxIndexBytes = 12 * 1_000_000;
    private const int MaxRecordBytes = 16 * 1024 * 1024;
    private readonly SafeFileHandle handle;
    private readonly MulEntry?[] entries;
    private readonly Dictionary<AssetKind, IReadOnlyList<MulEntry>> catalogs;
    public string DataFileName { get; }

    private MulArchive(SafeFileHandle handle, MulEntry?[] entries, string fileName)
    {
        this.handle = handle;
        this.entries = entries;
        DataFileName = fileName;
        catalogs = Enum.GetValues<AssetKind>().ToDictionary(kind => kind,
            kind => (IReadOnlyList<MulEntry>)Array.AsReadOnly(entries.OfType<MulEntry>()
                .Where(entry => entry.Kind == kind).ToArray()));
    }

    public static MulArchive Open(string indexPath, string dataPath, bool gumps = false)
    {
        // Limit before allocating. Read through a bounded stream rather than File.ReadAllBytes.
        using var index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (index.Length == 0 || index.Length % 12 != 0 || index.Length > MaxIndexBytes)
            throw new InvalidDataException("Indice MUL non valido: attesi record di 12 byte.");
        var bytes = new byte[(int)index.Length];
        index.ReadExactly(bytes);
        var entries = new MulEntry?[bytes.Length / 12];
        for (int slot = 0; slot < entries.Length; slot++)
        {
            var record = bytes.AsSpan(slot * 12, 12);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(record);
            int length = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            int extra = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            if (offset == -1 || length == -1 || length == 0) continue;
            AssetKind kind = gumps ? AssetKind.Gump : slot < 0x4000 ? AssetKind.Land : AssetKind.Item;
            int id = kind == AssetKind.Item ? slot - 0x4000 : slot;
            entries[slot] = new MulEntry(kind, id, offset, length, extra);
        }
        var handle = File.OpenHandle(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.RandomAccess);
        try { return new MulArchive(handle, entries, Path.GetFileName(dataPath)); }
        catch { handle.Dispose(); throw; }
    }

    public IReadOnlyList<MulEntry> GetEntries(AssetKind kind) => catalogs[kind];

    public DecodedImage Read(AssetKind kind, int id)
    {
        long slot = kind == AssetKind.Item ? (long)id + 0x4000 : id;
        if (id < 0 || slot < 0 || slot >= entries.Length ||
            entries[(int)slot] is not { } entry || entry.Kind != kind)
            throw new KeyNotFoundException("ID non presente nell'archivio.");
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
        return kind switch
        {
            AssetKind.Land => MulImageDecoder.DecodeLand(data),
            AssetKind.Item => MulImageDecoder.DecodeStatic(data),
            AssetKind.Gump => MulImageDecoder.DecodeGump(data, (int)((uint)entry.Extra >> 16), entry.Extra & 0xFFFF),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public void Dispose() => handle.Dispose();
}
