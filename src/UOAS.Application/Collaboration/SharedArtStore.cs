using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiddler.Formats;
using Fiddler.Formats.Art;
using FiddlerWebApp.Services.Users;

namespace UOAS.Application.Collaboration;

public sealed record ArtSnapshot(string Archive, long Revision, IReadOnlyDictionary<int, long> Versions, ArtDocument Document);
public sealed record ArtClaim(EditLease Lease, long Version);

public sealed class SharedArtStore(string storageDirectory, ResourceLeaseManager leases)
{
    private sealed record SavedState(string Fingerprint, string? Directory, long Revision, Dictionary<int, long> Versions);
    private sealed class ArchiveState(string source, SavedState saved)
    {
        public string Source { get; } = source;
        public SavedState Saved = saved;
        public SemaphoreSlim Publish { get; } = new(1);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, ArchiveState> archives = [];
    private string Manifest(string key) => Path.Combine(storageDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    private static string Fingerprint(string directory) => string.Join(";", new DirectoryInfo(directory).EnumerateFiles()
        .Where(file => file.Name.Equals("art.mul", StringComparison.OrdinalIgnoreCase) || file.Name.Equals("artidx.mul", StringComparison.OrdinalIgnoreCase))
        .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).Select(file => $"{file.Name.ToLowerInvariant()}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
    private ArchiveState Get(string directory)
    {
        string key = SharedGumpStore.Normalize(directory);
        lock (gate)
        {
            if (archives.TryGetValue(key, out var known))
            {
                if (known.Saved.Fingerprint != Fingerprint(known.Source)) throw new InvalidOperationException("The source Art archive changed outside UOAS. Reopen it after reviewing the published versions.");
                return known;
            }
            string fingerprint = Fingerprint(directory);
            var saved = File.Exists(Manifest(key))
                ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(Manifest(key))) ?? throw new InvalidDataException("Invalid Art version manifest.")
                : new SavedState(fingerprint, null, 0, []);
            if (saved.Fingerprint != fingerprint) throw new InvalidOperationException("The Art source no longer matches its published version history.");
            var state = new ArchiveState(Path.GetFullPath(directory), saved);
            archives.Add(key, state);
            return state;
        }
    }
    public ArtSnapshot Open(string directory)
    {
        var state = Get(directory);
        lock (gate)
        {
            string folder = state.Saved.Directory ?? state.Source;
            var files = new DirectoryInfo(folder).EnumerateFiles().ToDictionary(file => file.Name, file => file.FullName, StringComparer.OrdinalIgnoreCase);
            return new(SharedGumpStore.Normalize(directory), state.Saved.Revision, new Dictionary<int, long>(state.Saved.Versions), ArtDocument.Open(files["artidx.mul"], files["art.mul"]));
        }
    }
    public long Version(string directory, int slot)
    { var state = Get(directory); lock (gate) return state.Saved.Versions.GetValueOrDefault(slot); }
    public static void Apply(ArtDocument document, int slot, byte[]? bytes)
    {
        var kind = slot < 0x4000 ? AssetKind.Land : AssetKind.Item;
        int id = slot < 0x4000 ? slot : slot - 0x4000;
        if (bytes is null) document.Remove(kind, id);
        else document.Put(kind, id, kind == AssetKind.Land ? LandDecoder.Decode(bytes) : StaticArtDecoder.Decode(bytes));
    }
    public async Task<(string Directory, long Revision)> PublishAsync(string directory, IReadOnlyDictionary<int, byte[]?> changes,
        IReadOnlyDictionary<int, ArtClaim> claims, string output, CancellationToken token)
    {
        if (changes.Count == 0) throw new InvalidOperationException("There are no Art drafts to publish.");
        var state = Get(directory);
        await state.Publish.WaitAsync(token);
        try
        {
            SavedState before;
            lock (gate) before = state.Saved;
            var required = changes.Keys.Select(slot => claims.TryGetValue(slot, out var claim) && claim.Lease.Asset ==
                new AssetKey(SharedGumpStore.Normalize(directory), slot < 0x4000 ? "Land" : "Item", slot < 0x4000 ? slot : slot - 0x4000)
                ? claim : throw new InvalidOperationException("Acquire every modified Art ID before publishing.")).ToArray();
            using var pinned = leases.Pin(required.Select(claim => claim.Lease).ToArray());
            foreach (int slot in changes.Keys)
                if (before.Versions.GetValueOrDefault(slot) != claims[slot].Version) throw new InvalidOperationException("An Art ID has a newer published version. Discard that ID's draft and refresh before editing again.");
            string result = await Task.Run(() =>
            {
                using var latest = Open(directory).Document;
                foreach (var pair in changes) { token.ThrowIfCancellationRequested(); Apply(latest, pair.Key, pair.Value); }
                return latest.Export(output);
            }, token);
            var versions = new Dictionary<int, long>(before.Versions);
            foreach (int slot in changes.Keys) versions[slot] = before.Revision + 1;
            var after = new SavedState(before.Fingerprint, result, before.Revision + 1, versions);
            lock (gate)
            {
                Directory.CreateDirectory(storageDirectory);
                string path = Manifest(SharedGumpStore.Normalize(directory));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(after)); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                state.Saved = after;
            }
            return (result, after.Revision);
        }
        finally { state.Publish.Release(); }
    }
}
