using System.IO.Compression;

namespace Fiddler.Formats.Containers.Uop;

public sealed record UopEntry(ulong Hash, long Offset, int StoredLength, int ExpandedLength, short Compression);

/// <summary>Bounded MYP v4/v5 reader, independent of resource names and pixel formats.</summary>
public sealed class UopArchive : IDisposable
{
    public const int MaxPayload = 32 * 1024 * 1024;
    private readonly FileStream stream;
    private readonly Dictionary<ulong, UopEntry> entries = [];
    private readonly object gate = new();
    public IReadOnlyCollection<UopEntry> Entries => entries.Values;
    public UopEntry GetEntry(ulong hash) => entries[hash];

    private UopArchive(FileStream stream)
    {
        this.stream = stream;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        Range(0, 28);
        if (reader.ReadUInt32() != 0x50594D) throw new InvalidDataException("Invalid UOP signature.");
        uint version = reader.ReadUInt32();
        if (version is not (4 or 5)) throw new InvalidDataException("Unsupported UOP version.");
        _ = reader.ReadUInt32();
        long next = reader.ReadInt64();
        _ = reader.ReadUInt32(); _ = reader.ReadUInt32();
        var visited = new HashSet<long>();
        int total = 0;
        while (next != 0)
        {
            if (next < 28 || !visited.Add(next)) throw new InvalidDataException("Invalid UOP block chain.");
            Range(next, 12); stream.Position = next;
            int count = reader.ReadInt32(); next = reader.ReadInt64();
            if (count < 0 || count > 100_000 || (total += count) > 200_000)
                throw new InvalidDataException("Too many UOP records.");
            Range(stream.Position, count * 34L);
            for (int i = 0; i < count; i++)
            {
                long offset = reader.ReadInt64(); int header = reader.ReadInt32();
                int stored = reader.ReadInt32(), expanded = reader.ReadInt32();
                ulong hash = reader.ReadUInt64(); _ = reader.ReadUInt32(); short flag = reader.ReadInt16();
                if (offset == 0) continue;
                if (header < 0 || stored < 0 || expanded < 0 || stored > MaxPayload || expanded > MaxPayload || offset > long.MaxValue - header)
                    throw new InvalidDataException("Invalid UOP payload size.");
                Range(offset + header, stored);
                if (!entries.TryAdd(hash, new UopEntry(hash, offset + header, stored, expanded, flag)))
                    throw new InvalidDataException("Duplicate hash in the UOP container.");
            }
        }
    }

    public static UopArchive Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { return new UopArchive(stream); }
        catch { stream.Dispose(); throw; }
    }

    public byte[] Read(ulong hash)
    {
        if (!entries.TryGetValue(hash, out var entry)) throw new KeyNotFoundException("UOP entry not found.");
        var stored = new byte[entry.StoredLength];
        lock (gate) { stream.Position = entry.Offset; stream.ReadExactly(stored); }
        if (entry.Compression == 0) return stored;
        if (entry.Compression is not (1 or 3)) throw new InvalidDataException($"Unsupported UOP compression {entry.Compression}.");
        using var compressed = new MemoryStream(stored, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var expanded = new byte[entry.ExpandedLength];
        zlib.ReadExactly(expanded);
        if (zlib.ReadByte() != -1) throw new InvalidDataException("Unexpected decompressed UOP length.");
        return entry.Compression == 3 ? MythicDecoder.Decode(expanded, MaxPayload) : expanded;
    }

    public byte[] ReadStoredPrefix(ulong hash, int count)
    {
        var entry = entries[hash];
        if (entry.Compression != 0 || count < 0 || count > entry.StoredLength)
            throw new InvalidDataException("Invalid Gump UOP header.");
        var result = new byte[count];
        lock (gate) { stream.Position = entry.Offset; stream.ReadExactly(result); }
        return result;
    }

    private void Range(long offset, long count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
            throw new InvalidDataException("UOP offset or length extends beyond the file.");
    }
    public void Dispose() => stream.Dispose();
}
