using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;
using FiddlerWebApp.Services;
using FiddlerWebApp.Services.Users;
using UOAS.Contracts;

namespace UOAS.Application.Collaboration;

public sealed class AssetWorkspace : IDisposable
{
    private readonly SharedArtStore artStore;
    private readonly SharedGumpStore gumpStore;
    private readonly ResourceLeaseManager leases;
    private readonly WorkspaceUser user;
    private readonly string sessionId;
    private readonly SharedDocumentStore documentStore;
    private string? worldKey;
    private GumpDocument? referenceSnapshot;
    private IReadOnlyList<GumpComparison>? comparison;
    private sealed record CatalogSnapshot(long Version, IReadOnlyList<MulEntry> Entries, IReadOnlyDictionary<int, MulEntry> ById);
    private readonly Dictionary<ResourceKind, CatalogSnapshot> catalogs = [];
    private ArtSnapshot? art;
    private SharedGumpSnapshot? gumps;
    private readonly Dictionary<int, ArtClaim> artClaims = [];
    private readonly Dictionary<int, GumpEditClaim> gumpClaims = [];
    public Guid Id { get; } = Guid.NewGuid();
    public string Directory { get; }
    public string ArchiveKey { get; }
    public long Version { get; private set; } = 1;
    public SemaphoreSlim Gate { get; } = new(1);
    public FiddlerWebApp.Services.WorldAssetWorkspace World { get; } = new();
    public FiddlerWebApp.Services.AnimationWorkspace Animations { get; } = new();
    public ClientResourceWorkspace Resources { get; } = new();
    public void OpenMulti(int rowSize)
    { string key = "multi:" + rowSize; if (worldKey == key && World.Multis is not null) return; World.OpenMultis(Directory, null, rowSize); worldKey = key; }
    public bool AutomaticPartialHue(ResourceKind kind, int id)
    {
        if (kind != ResourceKind.Item) return false;
        var file = SharedDocumentStore.Files(Directory).FirstOrDefault(file => file.Kind == DocumentKind.TileDataItem);
        if (file is null) return false;
        var open = Documents.Values.FirstOrDefault(document => document.Kind == file.Kind && document.FileName == file.FileName);
        if (open is not null) return open.Read(id).Value.GetProperty("partialHue").GetBoolean();
        using var snapshot = documentStore.Create(Directory, file.FileName, file.Kind, sessionId, user);
        return snapshot.Read(id).Value.GetProperty("partialHue").GetBoolean();
    }
    public Dictionary<Guid, RecordWorkspace> Documents { get; } = [];
    public AssetWorkspace(string directory, string sessionId, WorkspaceUser user, SharedArtStore artStore, SharedGumpStore gumpStore, ResourceLeaseManager leases, SharedDocumentStore documentStore)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Enter an absolute client folder path on the backend.");
        Directory = Path.GetFullPath(directory); ArchiveKey = SharedGumpStore.Normalize(directory);
        this.sessionId = sessionId + ":" + Id.ToString("N"); this.user = user; this.artStore = artStore; this.gumpStore = gumpStore; this.leases = leases;
        this.documentStore = documentStore;
        try { Refresh(); }
        catch { Dispose(); throw; }
    }
    public WorkspaceState Info() => new(Id, Directory, Version, Math.Max(art?.Revision ?? 0, gumps?.Revision ?? 0), (art?.Document.DirtyCount ?? 0) + (gumps?.Document.DirtyCount ?? 0) + Documents.Values.Sum(document => document.DraftCount), art?.Document.DirtyCount ?? 0, gumps?.Document.DirtyCount ?? 0);
    public void Heartbeat() { leases.RenewSession(sessionId); foreach (var document in Documents.Values) document.Heartbeat(); }
    public DocumentInfo OpenDocument(DocumentKind kind, string file)
    {
        var existing = Documents.Values.FirstOrDefault(document => document.Kind == kind && document.FileName == file);
        if (existing is not null) return existing.Info();
        if (Documents.Count >= 12) throw new InvalidOperationException("Close a document before opening another.");
        var next = documentStore.Create(Directory, file, kind, sessionId, user); Documents.Add(next.Id, next); return next.Info();
    }
    private static AssetKind Kind(ResourceKind kind) => kind switch { ResourceKind.Land => AssetKind.Land, ResourceKind.Item => AssetKind.Item, ResourceKind.Gump => AssetKind.Gump, _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static int ArtSlot(ResourceKind kind, int id) => kind == ResourceKind.Item ? id + 0x4000 : id;
    private static void Validate(ResourceKind kind, int id)
    {
        int max = kind switch { ResourceKind.Land => 0x4000, ResourceKind.Item => 1_000_000 - 0x4000, ResourceKind.Gump => GumpUopReader.MaxSlots, _ => 0 };
        if (id < 0 || id >= max) throw new ArgumentOutOfRangeException(nameof(id));
    }
    private CatalogSnapshot Catalog(ResourceKind kind)
    {
        if (catalogs.TryGetValue(kind, out var known) && known.Version == Version) return known;
        var entries = kind == ResourceKind.Gump ? gumps?.Document.Entries ?? [] : art?.Document.Entries(Kind(kind)) ?? [];
        var next = new CatalogSnapshot(Version, entries, entries.ToDictionary(entry => entry.Id));
        catalogs[kind] = next; return next;
    }
    public IReadOnlyList<MulEntry> Entries(ResourceKind kind) => Catalog(kind).Entries;
    public bool Modified(ResourceKind kind, int id) => kind == ResourceKind.Gump ? gumps?.Document.IsModified(id) == true : art?.Document.IsModified(Kind(kind), id) == true;
    public IReadOnlyList<ResourceLock> Locks() => leases.List(ArchiveKey).Select(lease =>
        new ResourceLock(Enum.Parse<ResourceKind>(lease.Asset.Kind), lease.Asset.Id, lease.UserName, lease.SessionId == sessionId, lease.Expires, lease.Saving)).ToArray();
    public ResourceLock? Lock(ResourceKind kind, int id) => Locks().FirstOrDefault(lease => lease.Kind == kind && lease.Id == id);
    public async Task CompareGumps(string reference, CancellationToken token)
    {
        if (gumps is null) throw new InvalidOperationException("Open a Gump archive first.");
        var source = gumpStore.Open(reference).Document;
        try
        {
            var result = await Task.Run(() => GumpComparer.Compare(gumps.Document, source, token), token);
            referenceSnapshot?.Dispose(); referenceSnapshot = source; comparison = result;
        }
        catch { source.Dispose(); throw; }
    }
    public ComparisonPage Comparison(int offset, int limit, bool differencesOnly)
    {
        if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("Choose a page size from 1 to 100.");
        var rows = comparison ?? throw new InvalidOperationException("Compare a reference library first.");
        var filtered = rows.Where(row => !differencesOnly || row.Difference != GumpDifference.Equal).ToArray();
        return new(filtered.Length, rows.GroupBy(row => row.Difference.ToString()).ToDictionary(group => group.Key, group => group.Count()),
            filtered.Skip(offset).Take(limit).Select(row => new ComparisonRow(row.Id, row.Difference.ToString(), row.Detail)).ToArray());
    }
    public DecodedImage ReadReference(int id)
    { return (referenceSnapshot ?? throw new InvalidOperationException("Compare a reference library first.")).Read(id); }
    public void CopyReference(int sourceId, int targetId)
    {
        var lease = Required(ResourceKind.Gump, targetId);
        using var pin = leases.Pin([lease]);
        var reference = referenceSnapshot ?? throw new InvalidOperationException("Compare a reference library first.");
        var record = reference.ReadRecord(sourceId);
        leases.ExecuteOwned(lease, () => { gumps!.Document.Put(targetId, record); Version++; });
    }
    public AssetPage Page(ResourceKind kind, int offset, int limit, bool includeEmpty, int? id)
    {
        if (offset < 0 || limit < 1 || limit > 100) throw new ArgumentException("Use a page size from 1 to 100 and a nonnegative offset.");
        var catalog = Catalog(kind);
        var entries = catalog.ById;
        int slots = kind switch { ResourceKind.Gump => gumps?.Document.SlotCount ?? 0, ResourceKind.Land => Math.Min(0x4000, art?.Document.SlotCount ?? 0), _ => Math.Max(0, (art?.Document.SlotCount ?? 0) - 0x4000) };
        IEnumerable<int> ids = includeEmpty ? Enumerable.Range(0, slots) : catalog.Entries.Select(entry => entry.Id);
        if (id is { } requested) { Validate(kind, requested); ids = slots > 0 && (includeEmpty || entries.ContainsKey(requested)) ? [requested] : []; }
        int total = id is null ? includeEmpty ? slots : entries.Count : ids.Count();
        var locks = Locks().Where(lease => lease.Kind == kind).ToDictionary(lease => lease.Id);
        return new(offset, limit, total, ids.Skip(offset).Take(limit).Select(value =>
            new AssetSlot(kind, value, !entries.ContainsKey(value), Modified(kind, value), entries.GetValueOrDefault(value)?.Length ?? 0, locks.GetValueOrDefault(value))).ToArray());
    }
    public DecodedImage Read(ResourceKind kind, int id)
    {
        Validate(kind, id);
        return kind == ResourceKind.Gump ? (gumps?.Document.Read(id) ?? throw new KeyNotFoundException("No Gump archive is open."))
            : art?.Document.Read(Kind(kind), id) ?? throw new KeyNotFoundException("No Art archive is open.");
    }
    public AssetDetails Details(ResourceKind kind, int id)
    {
        Validate(kind, id);
        bool empty = !Catalog(kind).ById.ContainsKey(id);
        var image = empty ? null : Read(kind, id);
        return new(kind, id, empty, Modified(kind, id), image?.Width ?? 0, image?.Height ?? 0, Lock(kind, id));
    }
    public ResourceLock Acquire(ResourceKind kind, int id)
    {
        Validate(kind, id);
        if (kind == ResourceKind.Gump && gumps is null || kind != ResourceKind.Gump && art is null) throw new InvalidOperationException("Open the matching archive first.");
        if (kind == ResourceKind.Gump)
        {
            if (gumpClaims.ContainsKey(id) && !Modified(kind, id) && Lock(kind, id)?.Own != true) gumpClaims.Remove(id);
            if (gumpClaims.TryGetValue(id, out var old) && old.Version != gumpStore.Version(Directory, id)) throw new InvalidOperationException("This Gump has a newer published version. Discard its draft before acquiring it again.");
            if (gumps!.Versions.GetValueOrDefault(id) != gumpStore.Version(Directory, id)) Refresh();
            var lease = leases.Acquire(new(ArchiveKey, kind.ToString(), id), sessionId, user);
            gumpClaims[id] = new(lease, old?.Version ?? gumps!.Versions.GetValueOrDefault(id));
        }
        else
        {
            int slot = ArtSlot(kind, id);
            if (artClaims.ContainsKey(slot) && !Modified(kind, id) && Lock(kind, id)?.Own != true) artClaims.Remove(slot);
            if (artClaims.TryGetValue(slot, out var old) && old.Version != artStore.Version(Directory, slot)) throw new InvalidOperationException("This Art ID has a newer published version. Discard its draft before acquiring it again.");
            if (art!.Versions.GetValueOrDefault(slot) != artStore.Version(Directory, slot)) Refresh();
            var lease = leases.Acquire(new(ArchiveKey, kind.ToString(), id), sessionId, user);
            artClaims[slot] = new(lease, old?.Version ?? art!.Versions.GetValueOrDefault(slot));
        }
        return Lock(kind, id)!;
    }
    private EditLease Required(ResourceKind kind, int id)
    {
        Validate(kind, id);
        return kind == ResourceKind.Gump
            ? gumpClaims.TryGetValue(id, out var gump) ? gump.Lease : throw new InvalidOperationException("Acquire this ID before changing it.")
            : artClaims.TryGetValue(ArtSlot(kind, id), out var item) ? item.Lease : throw new InvalidOperationException("Acquire this ID before changing it.");
    }
    public void Put(ResourceKind kind, int id, byte[] png)
    {
        var lease = Required(kind, id);
        using var pin = leases.Pin([lease]);
        var image = Fiddler.Graphics.Gumps.GumpImageImporter.ImportImage(png);
        leases.ExecuteOwned(lease, () =>
        {
            if (kind == ResourceKind.Gump) gumps!.Document.Put(id, GumpEncoder.Encode(image));
            else art!.Document.Put(Kind(kind), id, image);
            Version++;
        });
    }
    public void Remove(ResourceKind kind, int id)
    {
        if (kind == ResourceKind.Gump) throw new ArgumentException("Gump removal is not supported by this archive editor.");
        leases.ExecuteOwned(Required(kind, id), () => { art!.Document.Remove(Kind(kind), id); Version++; });
    }
    public void Release(ResourceKind kind, int id, bool discard)
    {
        Validate(kind, id);
        EditLease? claim = kind == ResourceKind.Gump ? gumpClaims.GetValueOrDefault(id)?.Lease : artClaims.GetValueOrDefault(ArtSlot(kind, id))?.Lease;
        if (claim is null) return;
        if (Modified(kind, id) && !discard) throw new InvalidOperationException("Publish or discard this ID's draft before releasing it.");
        if (discard)
        {
            if (kind == ResourceKind.Gump) gumps!.Document.Undo(id); else art!.Document.Undo(Kind(kind), id);
            Version++;
        }
        leases.Release(claim);
        if (kind == ResourceKind.Gump) gumpClaims.Remove(id); else artClaims.Remove(ArtSlot(kind, id));
    }
    public void Refresh()
    {
        var files = new DirectoryInfo(Directory).EnumerateFiles().Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ArtSnapshot? nextArt = null; SharedGumpSnapshot? nextGumps = null;
        try
        {
            if (files.Contains("art.mul") && files.Contains("artidx.mul"))
            {
                nextArt = artStore.Open(Directory);
                if (art is not null) foreach (var pair in art.Document.GetChanges()) SharedArtStore.Apply(nextArt.Document, pair.Key, pair.Value);
            }
            if (files.Contains("gumpartlegacymul.uop") || files.Contains("gumpart.mul") && files.Contains("gumpidx.mul"))
            {
                nextGumps = gumpStore.Open(Directory);
                if (gumps is not null) foreach (var pair in gumps.Document.GetChanges()) nextGumps.Document.Put(pair.Key, pair.Value);
            }
        }
        catch { nextArt?.Document.Dispose(); nextGumps?.Document.Dispose(); throw; }
        art?.Document.Dispose(); gumps?.Document.Dispose(); art = nextArt; gumps = nextGumps; catalogs.Clear(); World.Dispose(); Animations.Dispose(); worldKey = null; Version++;
    }
    public async Task<ExportResult> Publish(ResourceKind kind, string output, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Enter an absolute output folder path on the backend.");
        ExportResult result;
        if (kind == ResourceKind.Gump)
        {
            var saved = await gumpStore.SaveAsync(Directory, gumps ?? throw new InvalidOperationException("No Gump archive."), gumps.Document.GetChanges(), gumpClaims, sessionId, output, token);
            gumps.Document.Discard(); foreach (var claim in gumpClaims.Values) leases.Release(claim.Lease); gumpClaims.Clear();
            result = new(saved.Directory, saved.Revision);
        }
        else
        {
            var saved = await artStore.PublishAsync(Directory, art?.Document.GetChanges() ?? throw new InvalidOperationException("No Art archive."), artClaims, output, token);
            art.Document.Discard(); foreach (var claim in artClaims.Values) leases.Release(claim.Lease); artClaims.Clear();
            result = new(saved.Directory, saved.Revision);
        }
        Refresh(); return result;
    }
    public void Dispose()
    {
        foreach (var claim in artClaims.Values) leases.Release(claim.Lease);
        foreach (var claim in gumpClaims.Values) leases.Release(claim.Lease);
        artClaims.Clear(); gumpClaims.Clear(); art?.Document.Dispose(); gumps?.Document.Dispose(); World.Dispose(); Animations.Dispose();
        referenceSnapshot?.Dispose();
        foreach (var document in Documents.Values) document.Dispose(); Documents.Clear();
    }
}
