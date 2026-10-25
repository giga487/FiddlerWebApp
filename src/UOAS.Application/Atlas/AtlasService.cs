using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fiddler.Formats.Maps;
using SkiaSharp;
using UOAS.Application.Storage;
using UOAS.Contracts;

namespace UOAS.Application.Atlas;

/// <summary>Bounded radar images derived from read-only archives, shared across sessions.</summary>
public sealed class AtlasService(WorkspaceSettingsStore settings, string cacheDirectory, CancellationToken stopping)
{
    private readonly SemaphoreSlim workers = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> jobs = new();
    private int completed;
    private sealed record Source(AtlasMap Map, string File, string Palette);
    private static readonly string[] Names = ["Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur"];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Stamp(string file) { var info = new FileInfo(file); return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"; }
    private static void Verify(Source source)
    { if (Hash($"atlas-radar-v1|{Stamp(source.File)}|{Stamp(source.Palette)}|{source.Map.Width}|{source.Map.Height}") != source.Map.Revision) throw new InvalidOperationException("Map files changed during rendering. Refresh the atlas."); }
    private Source Resolve(string library, int facet)
    {
        if (facet is < 0 or > 255) throw new ArgumentException("Map file IDs must be from 0 to 255.");
        string directory = settings.Library(library);
        var files = Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path)!, path => path, StringComparer.OrdinalIgnoreCase);
        string file = files.GetValueOrDefault($"map{facet}legacymul.uop") ?? files.GetValueOrDefault($"map{facet}.mul") ?? throw new KeyNotFoundException("Map archive not found in the selected library.");
        string palette = files.GetValueOrDefault("radarcol.mul") ?? throw new FileNotFoundException("Add radarcol.mul to this library to render the map.");
        using var archive = MapArchive.Open(file, facet);
        var custom = settings.Read().Libraries.Single(item => item.Id == library).Maps?.FirstOrDefault(map => map.FileId == facet);
        var detected = MapArchive.DetectSize(facet, archive.BlockCount);
        int width = custom?.Width > 0 ? custom.Width : detected?.Width ?? 0, height = custom?.Height > 0 ? custom.Height : detected?.Height ?? 0;
        if (width == 0 || height == 0) throw new ArgumentException("Set this map's width and height in Configuration; custom dimensions are never guessed.");
        long expected = (long)(width / 8) * (height / 8);
        if (archive.BlockCount != expected && archive.BlockCount != expected + 1) throw new ArgumentException("Map dimensions do not match the archive block count.");
        long paletteLength = new FileInfo(palette).Length;
        if (paletteLength is < 2 or > 131072 || paletteLength % 2 != 0) throw new InvalidDataException("radarcol.mul must contain at most 65,536 RGB555 colors.");
        string revision = Hash($"atlas-radar-v1|{Stamp(file)}|{Stamp(palette)}|{width}|{height}");
        return new(new(library, facet, custom?.Name ?? (facet < Names.Length ? Names[facet] : $"Map {facet}"), width, height, revision, true, "Terrain radar · statics and diff patches are not applied"), file, palette);
    }
    public AtlasCatalog Catalog()
    {
        var maps = new List<AtlasMap>(); var warnings = new List<string>();
        foreach (var library in settings.Read().Libraries)
        {
            try
            {
                int[] facets = Directory.EnumerateFiles(settings.Library(library.Id)).Select(Path.GetFileName).Select(name => Regex.Match(name!, @"^map(0|[1-9][0-9]{0,2})(?:\.mul|legacymul\.uop)$", RegexOptions.IgnoreCase)).Where(match => match.Success).Select(match => int.Parse(match.Groups[1].Value)).Where(id => id <= 255).Distinct().Order().ToArray();
                foreach (int facet in facets)
                {
                    try { maps.Add(Resolve(library.Id, facet).Map); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or InvalidOperationException)
                    { maps.Add(new(library.Id, facet, library.Maps?.FirstOrDefault(map => map.FileId == facet)?.Name ?? (facet < Names.Length ? Names[facet] : $"Map {facet}"), 0, 0, "", false, ex.Message)); }
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { warnings.Add(library.Name + ": " + ex.Message); }
        }
        return new(maps, warnings);
    }
    private static uint[] Palette(string file)
    {
        byte[] bytes = File.ReadAllBytes(file); var colors = new uint[bytes.Length / 2];
        for (int i = 0; i < colors.Length; i++)
        {
            uint color = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
            uint Expand(int shift) { uint c = color >> shift & 31; return c << 3 | c >> 2; }
            colors[i] = 0xFF000000 | Expand(10) << 16 | Expand(5) << 8 | Expand(0);
        }
        return colors;
    }
    private static byte[] Encode(int width, int height, SKColor[] pixels)
    { using var bitmap = new SKBitmap(width, height); bitmap.Pixels = pixels; using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray(); }
    private static uint Color(ReadOnlySpan<byte> block, int index, uint[] palette)
    { int id = BinaryPrimitives.ReadUInt16LittleEndian(block[(4 + index * 3)..]); if (id >= palette.Length) throw new InvalidDataException("A land tile ID is outside radarcol.mul."); return palette[id]; }
    private byte[] BuildBase(Source source)
    {
        using var archive = MapArchive.Open(source.File, source.Map.FileId); var palette = Palette(source.Palette);
        int width = source.Map.Width / 8, height = source.Map.Height / 8; var pixels = new SKColor[width * height];
        for (int first = 0; first < width * height; first += 4096)
        {
            stopping.ThrowIfCancellationRequested();
            int count = Math.Min(4096, width * height - first); byte[] blocks = archive.ReadTerrainBlocks(first, count);
            for (int index = 0; index < count; index++)
            {
                int bx = (first + index) / height, by = (first + index) % height;
                var block = blocks.AsSpan(index * 196, 196); uint r = 0, g = 0, b = 0;
                for (int i = 0; i < 64; i++) { uint color = Color(block, i, palette); r += color >> 16 & 255; g += color >> 8 & 255; b += color & 255; }
                pixels[by * width + bx] = new((byte)((r + 32) / 64), (byte)((g + 32) / 64), (byte)((b + 32) / 64));
            }
        }
        Verify(source); return Encode(width, height, pixels);
    }
    private static byte[] FromBase(byte[] png, int scale, int tileX, int tileY)
    {
        using var bitmap = SKBitmap.Decode(png) ?? throw new InvalidDataException("Invalid atlas cache image.");
        var source = bitmap.Pixels; var pixels = new SKColor[256 * 256]; int factor = scale / 8;
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
        {
            int startX = (tileX * 256 + x) * factor, startY = (tileY * 256 + y) * factor;
            if (startX >= bitmap.Width || startY >= bitmap.Height) continue;
            uint r = 0, g = 0, b = 0, count = 0;
            for (int sy = startY; sy < Math.Min(startY + factor, bitmap.Height); sy++) for (int sx = startX; sx < Math.Min(startX + factor, bitmap.Width); sx++)
            { var color = source[sy * bitmap.Width + sx]; r += color.Red; g += color.Green; b += color.Blue; count++; }
            pixels[y * 256 + x] = new((byte)((r + count / 2) / count), (byte)((g + count / 2) / count), (byte)((b + count / 2) / count));
        }
        return Encode(256, 256, pixels);
    }
    private byte[] Detail(Source source, int scale, int tileX, int tileY)
    {
        using var archive = MapArchive.Open(source.File, source.Map.FileId); var palette = Palette(source.Palette);
        int originX = tileX * 256 * scale, originY = tileY * 256 * scale;
        var r = new uint[256 * 256]; var g = new uint[r.Length]; var b = new uint[r.Length]; var count = new uint[r.Length];
        int endX = Math.Min(source.Map.Width, originX + 256 * scale), endY = Math.Min(source.Map.Height, originY + 256 * scale);
        for (int bx = originX / 8; bx < (endX + 7) / 8; bx++)
        {
            stopping.ThrowIfCancellationRequested();
            for (int by = originY / 8; by < (endY + 7) / 8; by++)
            {
                byte[] block = archive.ReadTerrainBlock(bx * (source.Map.Height / 8) + by);
                for (int i = 0; i < 64; i++)
                {
                    int x = bx * 8 + i % 8, y = by * 8 + i / 8;
                    if (x < originX || y < originY || x >= endX || y >= endY) continue;
                    int target = (y - originY) / scale * 256 + (x - originX) / scale; uint color = Color(block, i, palette);
                    r[target] += color >> 16 & 255; g[target] += color >> 8 & 255; b[target] += color & 255; count[target]++;
                }
            }
        }
        var pixels = new SKColor[r.Length];
        for (int i = 0; i < pixels.Length; i++) if (count[i] > 0) pixels[i] = new((byte)((r[i] + count[i] / 2) / count[i]), (byte)((g[i] + count[i] / 2) / count[i]), (byte)((b[i] + count[i] / 2) / count[i]));
        Verify(source); return Encode(256, 256, pixels);
    }
    private async Task<byte[]> Cached(string key, Func<byte[]> build)
    {
        Directory.CreateDirectory(cacheDirectory); string file = Path.Combine(cacheDirectory, Hash(key) + ".png");
        if (File.Exists(file)) { try { return await File.ReadAllBytesAsync(file, stopping); } catch (FileNotFoundException) { } }
        if (jobs.Count >= 64 && !jobs.ContainsKey(key)) throw new InvalidOperationException("The atlas is preparing other maps. Retry shortly.");
        var lazy = jobs.GetOrAdd(key, _ => new(async () =>
        {
            await workers.WaitAsync(stopping);
            try
            {
                byte[] bytes = await Task.Run(build, stopping); string staging = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(staging, bytes, stopping); File.Move(staging, file, true); }
                finally { if (File.Exists(staging)) File.Delete(staging); }
                if (Interlocked.Increment(ref completed) % 32 == 0) Prune(); return bytes;
            }
            finally { workers.Release(); }
        }));
        try { return await lazy.Value; }
        finally { if (lazy.IsValueCreated && lazy.Value.IsCompleted) jobs.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, lazy)); }
    }
    private void Prune()
    {
        var files = new DirectoryInfo(cacheDirectory).EnumerateFiles("*.png").OrderBy(file => file.LastWriteTimeUtc).ToArray(); long total = files.Sum(file => file.Length);
        foreach (var file in files) { if (total <= 512L * 1024 * 1024) break; try { long length = file.Length; file.Delete(); total -= length; } catch (IOException) { } }
    }
    public async Task<byte[]> Tile(string library, int facet, string revision, int scale, int x, int y, CancellationToken token)
    {
        if (scale < 1 || scale > 64 || (scale & (scale - 1)) != 0 || x < 0 || y < 0) throw new ArgumentException("Choose a power-of-two scale from 1 to 64 and nonnegative tile coordinates.");
        var source = Resolve(library, facet);
        if (source.Map.Revision != revision) throw new InvalidOperationException("Map files or dimensions changed. Refresh the atlas.");
        if ((long)x * 256 * scale >= source.Map.Width || (long)y * 256 * scale >= source.Map.Height) throw new ArgumentException("The image tile is outside the map.");
        if (scale < 8) return await Cached($"{revision}/{scale}/{x}/{y}", () => Detail(source, scale, x, y)).WaitAsync(token);
        byte[] overview = await Cached(revision + "/base", () => BuildBase(source)).WaitAsync(token);
        return await Cached($"{revision}/{scale}/{x}/{y}", () => FromBase(overview, scale, x, y)).WaitAsync(token);
    }
    public async Task<byte[]> Thumbnail(string library, int facet, string revision, CancellationToken token)
    {
        var source = Resolve(library, facet);
        if (source.Map.Revision != revision) throw new InvalidOperationException("Refresh the atlas: this map changed.");
        byte[] overview = await Cached(revision + "/base", () => BuildBase(source)).WaitAsync(token);
        return await Cached(revision + "/thumbnail", () =>
        {
            using var bitmap = SKBitmap.Decode(overview); float factor = Math.Min(1, 512f / Math.Max(bitmap.Width, bitmap.Height));
            int width = Math.Max(1, (int)(bitmap.Width * factor)), height = Math.Max(1, (int)(bitmap.Height * factor));
            using var target = new SKBitmap(width, height); using var canvas = new SKCanvas(target); canvas.Clear(SKColors.Transparent); canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height));
            return Encode(width, height, target.Pixels);
        }).WaitAsync(token);
    }
}
