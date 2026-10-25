using System.Buffers.Binary;
using Fiddler.Formats.Containers.Mul;
using Fiddler.Formats.Containers.Uop;
using Microsoft.Win32.SafeHandles;

namespace Fiddler.Formats.Maps;

public sealed record MapLandTile(int X, int Y, int TileId, sbyte Z);
public sealed record MapStaticTile(int X, int Y, int ItemId, sbyte Z, ushort Hue);
public sealed record MapRegion(int X, int Y, int Size, IReadOnlyList<MapLandTile> Land, IReadOnlyList<MapStaticTile> Statics);

/// <summary>Reads bounded regions from column-major 8x8 map blocks and optional 7-byte static records.</summary>
public sealed class MapArchive : IDisposable
{
    private const int BlockBytes = 196, ChunkBlocks = 4096;
    private readonly SafeFileHandle? map;
    private readonly UopArchive? uop;
    private readonly List<UopEntry> chunks = [];
    private readonly IndexedMulFile? statics;
    private int cachedChunk = -1;
    private byte[]? chunkData;
    public long BlockCount { get; }
    public bool HasStatics => statics is not null;
    public string Format => uop is null ? "MUL" : "UOP";

    private MapArchive(string path, int facet, string? staticIndex, string? staticPath)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".uop", StringComparison.OrdinalIgnoreCase))
            {
                uop = UopArchive.Open(path);
                var table = uop.Entries.ToDictionary(e => e.Hash);
                for (int i = 0; i < 1025; i++)
                {
                    ulong hash = UopNameHash.HashFileName($"build/map{facet}legacymul/{i:D8}.dat");
                    if (!table.TryGetValue(hash, out var entry)) break;
                    if (entry.Compression is not (0 or 1)) throw new InvalidDataException("UOP maps support stored and zlib compression.");
                    int length = entry.Compression == 0 ? entry.StoredLength : entry.ExpandedLength;
                    if (length <= 0 || length % BlockBytes != 0 || length > ChunkBlocks * BlockBytes)
                        throw new InvalidDataException("Invalid map UOP chunk size.");
                    if (chunks.Count > 0 && Length(chunks[^1]) != ChunkBlocks * BlockBytes)
                        throw new InvalidDataException("Incomplete intermediate map UOP chunk.");
                    chunks.Add(entry); BlockCount += length / BlockBytes;
                }
                if (chunks.Count == 0 || chunks.Count != table.Count) throw new InvalidDataException("Map UOP chunks are missing, unknown or not consecutive.");
            }
            else
            {
                map = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
                long length = RandomAccess.GetLength(map);
                if (length == 0 || length % BlockBytes != 0) throw new InvalidDataException("MUL map is not aligned to 196-byte blocks.");
                BlockCount = length / BlockBytes;
            }
            if ((staticIndex is null) != (staticPath is null)) throw new InvalidDataException("Both staidx and statics are required for map statics.");
            if (staticIndex is not null)
            {
                // An empty index represents no statics; still check that the paired data file is readable.
                if (new FileInfo(staticIndex).Length == 0)
                { using var empty = File.Open(staticPath!, FileMode.Open, FileAccess.Read, FileShare.Read); }
                else statics = IndexedMulFile.Open(staticIndex, staticPath!);
            }
        }
        catch { Dispose(); throw; }
    }
    public static MapArchive Open(string path, int facet = 0, string? staticIndex = null, string? staticPath = null)
    {
        if (facet < 0 || facet > 255) throw new ArgumentOutOfRangeException(nameof(facet));
        return new(path, facet, staticIndex, staticPath);
    }
    private static int Length(UopEntry entry) => entry.Compression == 0 ? entry.StoredLength : entry.ExpandedLength;

    public static (int Width, int Height)? DetectSize(int facet, long blocks)
    {
        (int Width, int Height)[] candidates = facet switch
        {
            0 or 1 => [(6144, 4096), (7168, 4096)], 2 => [(2304, 1600)],
            3 => [(2560, 2048)], 4 => [(1448, 1448)], 5 => [(1280, 4096)], _ => []
        };
        foreach (var size in candidates)
            if (blocks == (long)(size.Width / 8) * (size.Height / 8) || blocks == (long)(size.Width / 8) * (size.Height / 8) + 1) return size;
        return null;
    }

    public MapRegion ReadRegion(int width, int height, int x, int y, int size = 8, bool includeStatics = true)
    {
        if (width < 8 || height < 8 || width > 16384 || height > 16384 || width % 8 != 0 || height % 8 != 0)
            throw new ArgumentException("Map dimensions must be multiples of 8, from 8 to 16384 tiles.");
        long expected = (long)(width / 8) * (height / 8);
        if (BlockCount != expected && BlockCount != expected + 1) throw new InvalidDataException("The specified dimensions do not match the map block count.");
        if (includeStatics && statics is not null && statics.SlotCount != expected)
            throw new InvalidDataException("The statics index does not match the map dimensions.");
        if (size < 1 || size > 16 || x < 0 || y < 0 || x > width - size || y > height - size)
            throw new ArgumentException("Region is outside the map (maximum side length: 16 tiles).");
        var land = new List<MapLandTile>(size * size);
        var items = new List<MapStaticTile>();
        for (int bx = x / 8; bx <= (x + size - 1) / 8; bx++)
            for (int by = y / 8; by <= (y + size - 1) / 8; by++)
            {
                int blockId = bx * (height / 8) + by;
                byte[] block = ReadBlock(blockId);
                for (int i = 0; i < 64; i++)
                {
                    int tx = bx * 8 + i % 8, ty = by * 8 + i / 8;
                    if (tx < x || tx >= x + size || ty < y || ty >= y + size) continue;
                    land.Add(new(tx, ty, BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4 + i * 3)), unchecked((sbyte)block[6 + i * 3])));
                }
                if (!includeStatics || statics is null || !statics.Contains(blockId)) continue;
                var data = statics.Read(blockId);
                if (data.Length % 7 != 0 || data.Length / 7 > 50_000) throw new InvalidDataException("Static block is invalid or too large.");
                for (int p = 0; p < data.Length; p += 7)
                {
                    if (data[p + 2] > 7 || data[p + 3] > 7) throw new InvalidDataException("Static local coordinates extend beyond the block.");
                    int tx = bx * 8 + data[p + 2], ty = by * 8 + data[p + 3];
                    if (tx < x || tx >= x + size || ty < y || ty >= y + size) continue;
                    if (items.Count >= 10_000) throw new InvalidDataException("Too many statics in the region: reduce the area.");
                    items.Add(new(tx, ty, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)), unchecked((sbyte)data[p + 4]), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 5))));
                }
            }
        return new(x, y, size, land.AsReadOnly(), items.AsReadOnly());
    }
    public byte[] ReadTerrainBlock(int id)
    { if (id < 0 || id >= BlockCount) throw new ArgumentOutOfRangeException(nameof(id)); return ReadBlock(id); }
    public byte[] ReadTerrainBlocks(int first, int count)
    {
        if (first < 0 || count is < 1 or > 4096 || (long)first + count > BlockCount) throw new ArgumentOutOfRangeException(nameof(count));
        var data = new byte[count * BlockBytes];
        if (map is null) { for (int i = 0; i < count; i++) ReadBlock(first + i).CopyTo(data, i * BlockBytes); return data; }
        int read = 0;
        while (read < data.Length)
        { int length = RandomAccess.Read(map, data.AsSpan(read), (long)first * BlockBytes + read); if (length == 0) throw new InvalidDataException("Truncated map blocks."); read += length; }
        return data;
    }
    private byte[] ReadBlock(int id)
    {
        var result = new byte[BlockBytes];
        if (map is not null)
        {
            int read = 0;
            while (read < result.Length)
            {
                int count = RandomAccess.Read(map, result.AsSpan(read), (long)id * BlockBytes + read);
                if (count == 0) throw new InvalidDataException("Truncated map block.");
                read += count;
            }
        }
        else
        {
            int chunk = id / ChunkBlocks, offset = id % ChunkBlocks * BlockBytes;
            if (cachedChunk != chunk) { chunkData = uop!.Read(chunks[chunk].Hash); cachedChunk = chunk; }
            if (offset > chunkData!.Length - BlockBytes) throw new InvalidDataException("Block extends beyond the UOP chunk.");
            chunkData.AsSpan(offset, BlockBytes).CopyTo(result);
        }
        return result;
    }
    public void Dispose() { map?.Dispose(); uop?.Dispose(); statics?.Dispose(); chunkData = null; }
}
