using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiddlerWebApp.Services.Users;
using UOAS.Contracts;

namespace UOAS.Application.Collaboration;

public sealed class SharedDocumentStore(string storage, ResourceLeaseManager leases)
{
    private sealed record Manifest(string Fingerprint, string? Directory, long Revision, Dictionary<string, long> Versions);
    private sealed class State(string source, Manifest manifest)
    {
        public string Source { get; } = source;
        public Manifest Saved = manifest;
        public SemaphoreSlim Publish { get; } = new(1);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, State> states = [];
    public static string Key(string root, string file) => SharedGumpStore.Normalize(root) + "/" + file.ToUpperInvariant();
    private static string Fingerprint(string path) { var file = new FileInfo(path); return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}"; }
    private string StatePath(string key) => Path.Combine(storage, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    public static string Resolve(string root, string file)
    {
        if (Path.GetFileName(file) != file || string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Choose a file from this library.");
        return new DirectoryInfo(root).EnumerateFiles().FirstOrDefault(candidate => candidate.Name.Equals(file, StringComparison.OrdinalIgnoreCase))?.FullName ?? throw new FileNotFoundException("Document file not found.");
    }
    public static IReadOnlyList<DocumentFile> Files(string root) => new DirectoryInfo(root).EnumerateFiles().SelectMany(file =>
    {
        string name = file.Name.ToLowerInvariant();
        if (name == "hues.mul") return new[] { new DocumentFile(DocumentKind.Hue, file.Name) };
        if (name == "tiledata.mul") return [new(DocumentKind.TileDataLand, file.Name), new(DocumentKind.TileDataItem, file.Name)];
        if (name.StartsWith("cliloc.", StringComparison.Ordinal)) return [new(DocumentKind.Cliloc, file.Name)];
        if (name == "fonts.mul" || name.StartsWith("unifont", StringComparison.Ordinal) && name.EndsWith(".mul", StringComparison.Ordinal)) return [new(DocumentKind.Font, file.Name)];
        return Array.Empty<DocumentFile>();
    }).ToArray();
    private State Get(string root, string file)
    {
        string key = Key(root, file);
        lock (gate)
        {
            if (states.TryGetValue(key, out var known))
            { if (known.Saved.Fingerprint != Fingerprint(known.Source)) throw new InvalidOperationException("The source document changed outside UOAS."); return known; }
            string path = Resolve(root, file);
            var saved = File.Exists(StatePath(key)) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(StatePath(key))) ?? throw new InvalidDataException("Invalid document manifest.") : new Manifest(Fingerprint(path), null, 0, []);
            if (saved.Fingerprint != Fingerprint(path)) throw new InvalidOperationException("The source document differs from its published history.");
            var state = new State(path, saved); states.Add(key, state); return state;
        }
    }
    internal (NativeDocument Document, long Revision, Dictionary<string, long> Versions) Open(string root, string file, DocumentKind kind)
    {
        var state = Get(root, file);
        lock (gate) return (new NativeDocument(kind, state.Saved.Directory is { } folder ? Resolve(folder, file) : state.Source), state.Saved.Revision, new(state.Saved.Versions));
    }
    public long Version(string root, string file, DocumentKind kind, int id)
    { var state = Get(root, file); lock (gate) return state.Saved.Versions.GetValueOrDefault($"{kind}:{id}"); }
    public RecordWorkspace Create(string root, string file, DocumentKind kind, string sessionId, WorkspaceUser user)
    {
        if (!Files(root).Contains(new DocumentFile(kind, file))) throw new ArgumentException("Choose an available document from this library.");
        return new(this, leases, root, file, kind, sessionId, user);
    }
    internal async Task<ExportResult> Publish(RecordWorkspace workspace, string output, CancellationToken token)
    {
        if (workspace.Changes.Count == 0) throw new InvalidOperationException("There are no document drafts to publish.");
        var state = Get(workspace.Root, workspace.FileName);
        await state.Publish.WaitAsync(token);
        try
        {
            Manifest before; lock (gate) before = state.Saved;
            using var pin = leases.Pin(workspace.Claims.Values.Select(claim => claim.Lease).ToArray());
            foreach (var pair in workspace.Changes)
                if (!workspace.Claims.TryGetValue(pair.Key, out var claim) || claim.Version != before.Versions.GetValueOrDefault($"{workspace.Kind}:{pair.Key}")) throw new InvalidOperationException("A record has a newer published version. Discard it and refresh before editing again.");
            var latest = Open(workspace.Root, workspace.FileName, workspace.Kind).Document;
            foreach (var pair in workspace.Changes) latest.Put(pair.Key, pair.Value);
            string result = await Task.Run(() => latest.Export(output), token);
            var versions = new Dictionary<string, long>(before.Versions);
            foreach (int id in workspace.Changes.Keys) versions[$"{workspace.Kind}:{id}"] = before.Revision + 1;
            var after = new Manifest(before.Fingerprint, result, before.Revision + 1, versions);
            lock (gate)
            {
                Directory.CreateDirectory(storage); string path = StatePath(Key(workspace.Root, workspace.FileName)), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(after)); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                state.Saved = after;
            }
            workspace.Published(); return new(result, after.Revision);
        }
        finally { state.Publish.Release(); }
    }
}

public sealed class RecordWorkspace : IDisposable
{
    internal sealed record Claim(EditLease Lease, long Version);
    private readonly SharedDocumentStore store;
    private readonly ResourceLeaseManager leases;
    private readonly WorkspaceUser user;
    private readonly string sessionId;
    private NativeDocument document = null!;
    private Dictionary<string, long> versions = [];
    private long revision;
    public Guid Id { get; } = Guid.NewGuid();
    public string Root { get; }
    public string FileName { get; }
    public DocumentKind Kind { get; }
    public int DraftCount => Changes.Count;
    internal Dictionary<int, JsonElement> Changes { get; } = [];
    internal Dictionary<int, Claim> Claims { get; } = [];
    internal RecordWorkspace(SharedDocumentStore store, ResourceLeaseManager leases, string root, string file, DocumentKind kind, string sessionId, WorkspaceUser user)
    { this.store = store; this.leases = leases; Root = root; FileName = file; Kind = kind; this.sessionId = sessionId + ":" + Id.ToString("N"); this.user = user; Refresh(); }
    public DocumentInfo Info() => new(Id, Kind, FileName, document.Ids.Union(Changes.Keys).Count(), Changes.Count, revision);
    public void Refresh()
    {
        var opened = store.Open(Root, FileName, Kind); document = opened.Document; revision = opened.Revision; versions = opened.Versions;
        foreach (var pair in Changes) document.Put(pair.Key, pair.Value);
    }
    public DocumentOwner? Owner(int id)
    {
        var lease = leases.List(SharedDocumentStore.Key(Root, FileName)).FirstOrDefault(lease => lease.Asset.Kind == Kind.ToString() && lease.Asset.Id == id);
        return lease is null ? null : new(lease.UserName, lease.SessionId == sessionId, lease.Expires);
    }
    public IReadOnlyList<DocumentLock> Locks() => leases.List(SharedDocumentStore.Key(Root, FileName))
        .Where(lease => lease.Asset.Kind == Kind.ToString()).Select(lease => new DocumentLock(lease.Asset.Id, lease.UserName, lease.SessionId == sessionId, lease.Expires)).ToArray();
    public DocumentRecord Read(int id) => new(id, document.Read(id), Changes.ContainsKey(id), Owner(id));
    public Fiddler.Formats.DecodedImage RenderText(int index, string text) => document.RenderText(index, text);
    public DocumentPage Page(int offset, int limit, int? id, string? search)
    {
        if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("Choose a page size from 1 to 100.");
        IEnumerable<int> ids = id is { } requested ? [requested] : document.Ids.Union(Changes.Keys).Order();
        // Text search is explicit; normal pagination does not render every font glyph.
        if (!string.IsNullOrEmpty(search)) ids = ids.Where(value => document.Read(value).ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
        var array = ids.ToArray(); return new(array.Length, array.Skip(offset).Take(limit).Select(Read).ToArray());
    }
    public DocumentOwner Acquire(int id)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        _ = document.Read(id);
        long current = store.Version(Root, FileName, Kind, id);
        if (Claims.ContainsKey(id) && !Changes.ContainsKey(id) && Owner(id)?.Own != true) Claims.Remove(id);
        if (Claims.TryGetValue(id, out var old) && old.Version != current) throw new InvalidOperationException("This record has a newer published version. Discard its draft before editing again.");
        if (versions.GetValueOrDefault($"{Kind}:{id}") != current) Refresh();
        var lease = leases.Acquire(new(SharedDocumentStore.Key(Root, FileName), Kind.ToString(), id), sessionId, user);
        Claims[id] = new(lease, old?.Version ?? current); return Owner(id)!;
    }
    public void Put(int id, JsonElement value)
    {
        if (!Claims.TryGetValue(id, out var claim)) throw new InvalidOperationException("Acquire this record before changing it.");
        if (Changes.Count >= 512 && !Changes.ContainsKey(id)) throw new InvalidOperationException("Publish or discard drafts before editing more records.");
        leases.ExecuteOwned(claim.Lease, () => { document.Put(id, value); Changes[id] = value.Clone(); });
    }
    public void Release(int id, bool discard)
    {
        if (Changes.ContainsKey(id) && !discard) throw new InvalidOperationException("Publish or discard the record before releasing it.");
        if (Claims.Remove(id, out var claim)) leases.Release(claim.Lease);
        if (discard) { Changes.Remove(id); Refresh(); }
    }
    public void Heartbeat() => leases.RenewSession(sessionId);
    internal void Published() { Changes.Clear(); foreach (var claim in Claims.Values) leases.Release(claim.Lease); Claims.Clear(); Refresh(); }
    public Task<ExportResult> Publish(string output, CancellationToken token) => store.Publish(this, output, token);
    public void Dispose() { foreach (var claim in Claims.Values) leases.Release(claim.Lease); Claims.Clear(); }
}
