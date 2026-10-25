using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiddler.Formats.Gumps;

namespace FiddlerWebApp.Services.Users;

public sealed record SharedGumpSnapshot(string Archive, string Fingerprint, long Revision, IReadOnlyDictionary<int, long> Versions, GumpDocument Document);
public sealed record GumpEditClaim(EditLease Lease, long Version);
public sealed record SharedGumpSave(string Directory, long Revision);

/// <summary>Publishes immutable complete versions and merges each user's drafts against the latest version.</summary>
public sealed class SharedGumpStore(string storageDirectory, ResourceLeaseManager leases)
{
    private sealed record SavedState(string Fingerprint, string? LatestDirectory, long Revision, Dictionary<int, long> Versions);
    private sealed class ArchiveState(string source, string fingerprint)
    {
        public readonly string Source = source;
        public readonly string Fingerprint = fingerprint;
        public readonly SemaphoreSlim Publish = new(1);
        public SavedState Saved = new(fingerprint, null, 0, []);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, ArchiveState> archives = new(StringComparer.Ordinal);
    public static string Normalize(string directory)
    {
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        var segments = new Stack<string>();
        while (folder.Parent is { } parent) { segments.Push(folder.Name); folder = parent; }
        string resolved = folder.FullName;
        while (segments.TryPop(out string? segment))
        {
            var child = new DirectoryInfo(Path.Combine(resolved, segment));
            resolved = child.ResolveLinkTarget(true)?.FullName ?? child.FullName;
        }
        string path = Path.TrimEndingDirectorySeparator(resolved);
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }
    private static string Fingerprint(string directory)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles().Where(f =>
            f.Name.Equals("gumpart.mul", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("gumpidx.mul", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Equals("gumpartlegacymul.uop", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        return string.Join(";", files.Select(f => $"{f.Name.ToLowerInvariant()}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
    }
    private string StatePath(string archive) => Path.Combine(storageDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(archive))) + ".json");
    private ArchiveState Get(string directory)
    {
        string key = Normalize(directory);
        lock (gate)
        {
            if (archives.TryGetValue(key, out var known))
            {
                if (known.Fingerprint != Fingerprint(known.Source)) throw new InvalidOperationException("The source archive changed outside the application. Restart the server and review the source before continuing.");
                return known;
            }
            var state = new ArchiveState(Path.GetFullPath(directory), Fingerprint(directory));
            if (File.Exists(StatePath(key)))
            {
                var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath(key))) ?? throw new InvalidDataException("Invalid shared Gump version manifest.");
                if (saved.Fingerprint != state.Fingerprint) throw new InvalidOperationException("The source archive no longer matches its shared version history. Review the source and version manifest before continuing.");
                state.Saved = saved;
            }
            archives.Add(key, state);
            return state;
        }
    }
    public SharedGumpSnapshot Open(string directory)
    {
        var state = Get(directory);
        lock (gate)
        {
            var saved = state.Saved;
            return new(Normalize(directory), state.Fingerprint, saved.Revision, new Dictionary<int, long>(saved.Versions), GumpDocument.Open(saved.LatestDirectory ?? state.Source));
        }
    }
    public long Version(string directory, int id)
    { var state = Get(directory); lock (gate) return state.Saved.Versions.GetValueOrDefault(id); }
    public async Task<SharedGumpSave> SaveAsync(string directory, SharedGumpSnapshot basis, IReadOnlyDictionary<int, GumpRecord> changes,
        IReadOnlyDictionary<int, GumpEditClaim> claims, string sessionId, string output, CancellationToken token)
    {
        if (changes.Count == 0) throw new InvalidOperationException("There are no drafts to publish.");
        var state = Get(directory);
        if (Normalize(directory) != basis.Archive || basis.Fingerprint != state.Fingerprint) throw new InvalidOperationException("The draft belongs to another archive version.");
        await state.Publish.WaitAsync(token);
        try
        {
            SavedState before;
            lock (gate) before = state.Saved;
            var required = changes.Keys.Select(id => claims.TryGetValue(id, out var claim) && claim.Lease.SessionId == sessionId &&
                claim.Lease.Asset == new AssetKey(basis.Archive, "Gump", id) ? claim : throw new InvalidOperationException($"Acquire Gump {id} before saving.")).ToArray();
            using var pinned = leases.Pin(required.Select(c => c.Lease).ToArray());
            foreach (var claim in required)
                if (before.Versions.GetValueOrDefault(claim.Lease.Asset.Id) != claim.Version)
                    throw new InvalidOperationException($"Gump {claim.Lease.Asset.Id} was published by another session. Your draft is preserved; discard that ID and refresh the shared version.");
            string result = await Task.Run(() =>
            {
                using var document = GumpDocument.Open(before.LatestDirectory ?? state.Source);
                foreach (var pair in changes) document.Put(pair.Key, pair.Value);
                return document.ExportMul(output, token);
            }, token);
            var versions = new Dictionary<int, long>(before.Versions);
            foreach (int id in changes.Keys) versions[id] = before.Revision + 1;
            var after = new SavedState(state.Fingerprint, result, before.Revision + 1, versions);
            lock (gate)
            {
                string path = StatePath(basis.Archive), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { Directory.CreateDirectory(storageDirectory); File.WriteAllText(temporary, JsonSerializer.Serialize(after)); File.Move(temporary, path, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException($"The export is complete at {result}, but publishing its shared manifest failed. No shared version was replaced.", ex); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                state.Saved = after;
            }
            return new(result, after.Revision);
        }
        finally { state.Publish.Release(); }
    }
}
