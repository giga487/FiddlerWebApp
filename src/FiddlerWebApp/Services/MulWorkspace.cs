using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;
using FiddlerWebApp.Services.Users;

namespace FiddlerWebApp.Services;

/// <summary>Session-scoped archive ownership. No global mutable client profile.</summary>
public sealed class MulWorkspace : IDisposable
{
    private readonly UserSession? user;
    private readonly SharedGumpStore? shared;
    private readonly ResourceLeaseManager? leases;
    private SharedGumpSnapshot? snapshot;
    private readonly Dictionary<int, GumpEditClaim> claims = [];
    public long SharedRevision => snapshot?.Revision ?? 0;
    public bool Collaborative => shared is not null;
    public string? ArchiveKey => snapshot?.Archive;
    public MulWorkspace() { }
    public MulWorkspace(UserSession user, SharedGumpStore shared, ResourceLeaseManager leases)
    { this.user = user; this.shared = shared; this.leases = leases; }
    private ArtDocument? art;
    public ArtDocument? Art => art;
    private GumpDocument? gumps;
    public GumpDocument? Gumps => gumps;
    public string CurrentDirectory { get; private set; } = "";
    public bool IsOpen => art is not null || gumps is not null;
    public string DisplayName { get; private set; } = "No archive open";

    public void Open(string directory, bool discardChanges = false)
    {
        user?.RequireUser();
        if ((gumps?.HasChanges == true || art?.HasChanges == true) && !discardChanges)
            throw new InvalidOperationException("There are graphics drafts: export or discard changes before opening another archive.");
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Configure the MUL data folder before opening the archive.");
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        if (!folder.Exists) throw new DirectoryNotFoundException("The MUL data folder does not exist.");
        var files = folder.EnumerateFiles().ToDictionary(file => file.Name, file => file.FullName,
            StringComparer.OrdinalIgnoreCase);
        ArtDocument? nextArt = null;
        GumpDocument? nextGumps = null;
        SharedGumpSnapshot? nextSnapshot = null;
        try
        {
            if (files.TryGetValue("artidx.mul", out var artIndex) && files.TryGetValue("art.mul", out var artData))
                nextArt = ArtDocument.Open(artIndex, artData);
            if (files.ContainsKey("gumpidx.mul") && files.ContainsKey("gumpart.mul") || files.ContainsKey("gumpartlegacymul.uop"))
            {
                nextSnapshot = shared?.Open(folder.FullName);
                nextGumps = nextSnapshot?.Document ?? GumpDocument.Open(folder.FullName);
            }
            if (nextArt is null && nextGumps is null)
                throw new InvalidDataException("No MUL pair found. Expected artidx.mul + art.mul or gumpidx.mul + gumpart.mul.");
        }
        catch
        {
            nextArt?.Dispose(); nextGumps?.Dispose();
            throw;
        }
        Dispose();
        art = nextArt; gumps = nextGumps;
        snapshot = nextSnapshot;
        DisplayName = folder.Name;
        CurrentDirectory = folder.FullName;
    }

    public IReadOnlyList<MulEntry> Entries(AssetKind kind) => kind switch
    {
        AssetKind.Land => art?.Entries(AssetKind.Land) ?? [],
        AssetKind.Item => art?.Entries(AssetKind.Item) ?? [],
        AssetKind.Gump => gumps?.Entries ?? [],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public IReadOnlyList<MulEntry> CatalogEntries(AssetKind kind, bool includeEmpty = true)
    {
        var entries = Entries(kind);
        if (!includeEmpty) return entries;
        int slots = kind switch
        {
            AssetKind.Land => Math.Min(0x4000, art?.SlotCount ?? 0),
            AssetKind.Item => Math.Max(0, (art?.SlotCount ?? 0) - 0x4000),
            AssetKind.Gump => gumps?.SlotCount ?? 0,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var occupied = entries.ToDictionary(entry => entry.Id);
        return Enumerable.Range(0, slots)
            .Select(id => occupied.TryGetValue(id, out var entry) ? entry : new MulEntry(kind, id, -1, 0, 0))
            .ToArray();
    }

    public DecodedImage Read(MulEntry entry) => (entry.Kind switch
    {
        AssetKind.Land => art?.Read(AssetKind.Land, entry.Id),
        AssetKind.Item => art?.Read(AssetKind.Item, entry.Id),
        AssetKind.Gump => gumps?.Read(entry.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(entry))
    }) ?? throw new InvalidOperationException("Archive is not open.");

    public EditLease BeginEdit(int id)
    {
        var actor = user?.RequireUser() ?? throw new InvalidOperationException("A signed-in user context is required for editing.");
        if (id < 0 || id >= GumpUopReader.MaxSlots || snapshot is null || shared is null || leases is null)
            throw new InvalidOperationException("Open a shared Gump workspace and select a valid ID.");
        if (claims.TryGetValue(id, out var unused) && gumps?.IsModified(id) != true && !OwnsEdit(id)) claims.Remove(id);
        if (claims.TryGetValue(id, out var old))
        {
            // A lost lease may be reacquired only if nobody published the ID in the meantime.
            if (shared.Version(CurrentDirectory, id) != old.Version)
                throw new InvalidOperationException("This ID has a newer shared version. Discard its draft and refresh before editing it again.");
        }
        else if (shared.Version(CurrentDirectory, id) != snapshot.Versions.GetValueOrDefault(id)) RefreshShared();
        var lease = leases.Acquire(new(snapshot!.Archive, "Gump", id), user.SessionId, actor);
        claims[id] = new(lease, old?.Version ?? snapshot.Versions.GetValueOrDefault(id));
        return lease;
    }
    public IReadOnlyList<EditLease> EditLeases() => snapshot is null || leases is null ? [] : leases.List(snapshot.Archive);
    public bool OwnsEdit(int id) => claims.TryGetValue(id, out var claim) && EditLeases().Any(l => l.Token == claim.Lease.Token && l.SessionId == user?.SessionId);
    public void PutGump(int id, GumpRecord record)
    {
        user?.RequireUser();
        if (gumps is null || leases is null || !claims.TryGetValue(id, out var claim)) throw new InvalidOperationException("Start editing this ID before changing it.");
        leases.ExecuteOwned(claim.Lease, () => gumps.Put(id, record));
    }
    public void UndoGump(int id)
    { user?.RequireUser(); gumps?.Undo(id); ReleaseEdit(id); }
    public void ReleaseEdit(int id)
    {
        if (gumps?.IsModified(id) == true) throw new InvalidOperationException("Save or undo this ID's draft before releasing it.");
        if (claims.Remove(id, out var claim)) leases?.Release(claim.Lease);
    }
    public void DiscardGumps()
    { user?.RequireUser(); gumps?.Discard(); ReleaseClaims(); }
    private void ReleaseClaims()
    { foreach (var claim in claims.Values) leases?.Release(claim.Lease); claims.Clear(); }
    public void RefreshShared()
    {
        user?.RequireUser();
        if (shared is null || snapshot is null) throw new InvalidOperationException("No shared Gump workspace is open.");
        var next = shared.Open(CurrentDirectory);
        try { foreach (var pair in gumps!.GetChanges()) next.Document.Put(pair.Key, pair.Value); }
        catch { next.Document.Dispose(); throw; }
        gumps!.Dispose(); snapshot = next; gumps = next.Document;
    }
    public async Task<SharedGumpSave> SaveSharedAsync(string output, CancellationToken token)
    {
        user?.RequireUser();
        if (shared is null || snapshot is null || gumps is null || user is null) throw new InvalidOperationException("No shared Gump workspace is open.");
        var result = await shared.SaveAsync(CurrentDirectory, snapshot, gumps.GetChanges(), new Dictionary<int, GumpEditClaim>(claims), user.SessionId, output, token);
        gumps.Discard(); ReleaseClaims(); RefreshShared();
        return result;
    }

    public void Dispose()
    {
        ReleaseClaims(); snapshot = null;
        art?.Dispose(); gumps?.Dispose();
        art = null; gumps = null;
    }
}
