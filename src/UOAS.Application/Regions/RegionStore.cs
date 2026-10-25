using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiddlerWebApp.Services.Users;
using UOAS.Contracts;

namespace UOAS.Application.Regions;

public sealed class RegionStore(string storage, ResourceLeaseManager leases)
{
    internal sealed record SavedRow(int Id, long Version, JsonElement Value);
    internal sealed record Saved(string Fingerprint, long Revision, int NextId, SavedRow[] Rows, string? ExportFile);
    internal sealed class State(string key, string file, string name, string manifest, Saved saved, string[] maps, string[] types)
    {
        public readonly object Gate = new();
        public string Key = key, File = file, Name = name, Manifest = manifest;
        public Saved Saved = saved;
        public string[] Maps = maps, Types = types;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, State> states = [];
    private static byte[] Read(string file, int limit = 16 * 1024 * 1024)
    {
        using var stream = System.IO.File.OpenRead(file);
        if (stream.Length > limit) throw new ArgumentException("The region file exceeds its size limit.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    internal static void Persist(State state, Saved saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(state.Manifest)!);
        string staging = state.Manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { System.IO.File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(saved)); System.IO.File.Move(staging, state.Manifest, true); }
        finally { if (System.IO.File.Exists(staging)) System.IO.File.Delete(staging); }
        state.Saved = saved;
    }
    public RegionWorkspace Open(string file, string name, string owner, string user, string[] configuredMaps, string[] configuredTypes)
    {
        file = Path.GetFullPath(file);
        if (new FileInfo(file).ResolveLinkTarget(true) is { } target) file = target.FullName;
        string key = SharedGumpStore.Normalize(Path.GetDirectoryName(file)!) + "/regions/" + (OperatingSystem.IsWindows() ? Path.GetFileName(file).ToUpperInvariant() : Path.GetFileName(file));
        lock (gate)
        {
            if (!states.TryGetValue(key, out var state))
            {
                byte[] source = Read(file); string fingerprint = Convert.ToHexString(SHA256.HashData(source));
                var rows = RegionDocument.Parse(source);
                string manifest = Path.Combine(storage, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
                Saved saved = System.IO.File.Exists(manifest) ? JsonSerializer.Deserialize<Saved>(Read(manifest, 32 * 1024 * 1024)) ?? throw new InvalidDataException("Invalid region manifest.") : new(fingerprint, 0, rows.Length, rows.Select((row, id) => new SavedRow(id, 0, row)).ToArray(), null);
                if (saved.Fingerprint != fingerprint) throw new InvalidOperationException("The configured source changed outside UOAS. Restore it or configure a separate region source before editing its published revisions.");
                var maps = rows.Select(row => RegionDocument.Text(row, "Map")).Concat(configuredMaps).Distinct(StringComparer.Ordinal).Order().ToArray();
                if (maps.Length == 0) maps = ["Felucca"];
                state = new(key, file, name, manifest, saved, maps, RegionDocument.Types.Concat(rows.Select(row => RegionDocument.Text(row, "$type", "Region"))).Concat(configuredTypes).Distinct().Order().ToArray());
                states.Add(key, state);
            }
            return new(state, leases, owner, user);
        }
    }
    internal static void VerifySource(State state)
    { if (Convert.ToHexString(SHA256.HashData(Read(state.File))) != state.Saved.Fingerprint) throw new InvalidOperationException("The source file changed externally. Publication was stopped; drafts are preserved."); }
}

public sealed class RegionWorkspace : IDisposable
{
    private readonly RegionStore.State state;
    private readonly ResourceLeaseManager leases;
    private readonly string owner;
    private readonly WorkspaceUser user;
    private Dictionary<int, RegionStore.SavedRow> baseline = [];
    private readonly Dictionary<int, EditLease> claims = [];
    private readonly Dictionary<int, JsonElement?> drafts = [];
    public Guid Id { get; } = Guid.NewGuid();
    public long Revision { get; private set; }
    public int DraftCount => drafts.Count;
    internal RegionWorkspace(RegionStore.State state, ResourceLeaseManager leases, string session, string name)
    { this.state = state; this.leases = leases; owner = session + ":regions:" + Id.ToString("N"); user = new(session, name, null, "Collaboration"); Refresh(); }
    public RegionCatalog Info() => new(Id, state.Name, Revision, Values().Count, drafts.Count, state.Maps, state.Types);
    private Dictionary<int, JsonElement> Values()
    {
        var values = baseline.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
        foreach (var (id, value) in drafts) { if (value is { } row) values[id] = row; else values.Remove(id); }
        return values;
    }
    public IReadOnlyList<DocumentLock> Locks() => leases.List(state.Key).Where(lease => lease.Asset.Kind == "Region").Select(lease => new DocumentLock(lease.Asset.Id, lease.UserName, lease.SessionId == owner, lease.Expires)).ToArray();
    private DocumentOwner? Owner(int id) { var held = Locks().FirstOrDefault(lease => lease.Id == id); return held is null ? null : new(held.UserName, held.Own, held.ExpiresAt); }
    private RegionSummary Summary(int id, JsonElement value, bool deleted = false) => new(id, RegionDocument.Text(value, "Name"), RegionDocument.Text(value, "Map"), RegionDocument.Text(value, "$type", "Region"), RegionDocument.Number(value, "Priority"), value.TryGetProperty("Parent", out var parent) && parent.ValueKind == JsonValueKind.Object ? RegionDocument.Text(parent, "Name") : null, drafts.ContainsKey(id), deleted, Owner(id));
    public RegionPage Page(int offset, int limit, string? map, string? search)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Choose a page size from 1 to 100.");
        var values = Values();
        foreach (var (id, value) in drafts.Where(pair => pair.Value is null)) if (baseline.TryGetValue(id, out var old)) values[id] = old.Value;
        var rows = values.Where(pair => (string.IsNullOrEmpty(map) || RegionDocument.Text(pair.Value, "Map") == map) && (string.IsNullOrEmpty(search) || RegionDocument.Text(pair.Value, "Name").Contains(search, StringComparison.OrdinalIgnoreCase) || RegionDocument.Text(pair.Value, "$type").Contains(search, StringComparison.OrdinalIgnoreCase))).OrderBy(pair => pair.Key).ToArray();
        return new(rows.Length, rows.Skip(offset).Take(limit).Select(pair => Summary(pair.Key, pair.Value, drafts.TryGetValue(pair.Key, out var draft) && draft is null)).ToArray());
    }
    public RegionRecord Read(int id)
    {
        if (drafts.TryGetValue(id, out var draft) && draft is { } edited) return new(Summary(id, edited), edited, RegionDocument.Areas(edited));
        if (!baseline.TryGetValue(id, out var row)) throw new KeyNotFoundException("Region not found in this catalog.");
        return new(Summary(id, row.Value, drafts.ContainsKey(id)), row.Value, RegionDocument.Areas(row.Value));
    }
    public DocumentOwner Acquire(int id)
    {
        lock (state.Gate)
        {
            if (!state.Saved.Rows.Any(row => row.Id == id)) throw new KeyNotFoundException("Region no longer exists. Refresh the catalog.");
            if (drafts.ContainsKey(id) && Owner(id)?.Own != true) throw new InvalidOperationException("This region draft lost its lock. Undo it before acquiring the latest record.");
            if (claims.Remove(id, out var previous)) leases.Release(previous);
            var lease = leases.Acquire(new(state.Key, "Region", id), owner, user);
            claims[id] = lease;
            if (!drafts.ContainsKey(id)) baseline[id] = state.Saved.Rows.Single(row => row.Id == id);
            return Owner(id)!;
        }
    }
    public RegionRecord Create()
    {
        if (drafts.Count >= 128) throw new InvalidOperationException("Publish or discard drafts before creating more regions.");
        lock (state.Gate)
        {
            int id = state.Saved.NextId;
            RegionStore.Persist(state, state.Saved with { NextId = checked(id + 1) });
            claims[id] = leases.Acquire(new(state.Key, "Region", id), owner, user);
            drafts[id] = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["$type"] = "BaseRegion", ["Map"] = state.Maps[0], ["Name"] = "New region " + id, ["Priority"] = 50, ["Area"] = new[] { new { x1 = 0, y1 = 0, z1 = -128, x2 = 10, y2 = 10, z2 = 128 } } });
            return Read(id);
        }
    }
    private EditLease Required(int id) => claims.GetValueOrDefault(id) ?? throw new InvalidOperationException("Acquire this region ID before changing it.");
    public void Put(int id, JsonElement value)
    {
        _ = Read(id); RegionDocument.Validate(value, state.Maps);
        if (!state.Types.Contains(RegionDocument.Text(value, "$type", "Region"))) throw new ArgumentException("Select a type registered by this server source.");
        if (drafts.Count >= 128 && !drafts.ContainsKey(id)) throw new InvalidOperationException("Publish or discard drafts before editing more regions.");
        leases.ExecuteOwned(Required(id), () => drafts[id] = value.Clone());
    }
    public void Delete(int id) { _ = Read(id); leases.ExecuteOwned(Required(id), () => drafts[id] = null); }
    public void Release(int id, bool discard)
    {
        if (drafts.ContainsKey(id) && !discard) throw new InvalidOperationException("Publish or discard the region draft before releasing its lock.");
        if (discard) drafts.Remove(id);
        if (claims.Remove(id, out var lease)) leases.Release(lease);
        lock (state.Gate) { if (state.Saved.Rows.FirstOrDefault(row => row.Id == id) is { } row) baseline[id] = row; else baseline.Remove(id); }
    }
    public void Refresh()
    {
        lock (state.Gate) { baseline = state.Saved.Rows.ToDictionary(row => row.Id); Revision = state.Saved.Revision; }
    }
    public RegionExport Publish(string output)
    {
        if (drafts.Count == 0) throw new InvalidOperationException("There are no region drafts to publish.");
        lock (state.Gate)
        {
            using var pin = leases.Pin(drafts.Keys.Select(Required).ToArray());
            RegionStore.VerifySource(state);
            var latest = state.Saved.Rows.ToDictionary(row => row.Id);
            foreach (int id in drafts.Keys)
                if ((latest.GetValueOrDefault(id)?.Version ?? -1) != (baseline.GetValueOrDefault(id)?.Version ?? -1)) throw new InvalidOperationException("A region has a newer published version. Undo its draft before editing again.");
            long revision = checked(state.Saved.Revision + 1);
            foreach (var (id, value) in drafts) { if (value is { } row) latest[id] = new(id, revision, row); else latest.Remove(id); }
            if (latest.Count > 10000) throw new ArgumentException("A source supports at most 10,000 regions.");
            byte[] bytes = RegionDocument.Export(latest.Values.OrderBy(row => row.Id).Select(row => row.Value));
            if (bytes.Length > 16 * 1024 * 1024) throw new ArgumentException("The published region document exceeds 16 MiB.");
            string folder = Path.Combine(output, "regions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "regions.json"); File.WriteAllBytes(file, bytes);
            RegionStore.Persist(state, state.Saved with { Revision = revision, Rows = latest.Values.ToArray(), ExportFile = file });
            foreach (var lease in claims.Values) leases.Release(lease); claims.Clear(); drafts.Clear(); Refresh();
            return new("regions.json", revision, Encoding.UTF8.GetString(bytes));
        }
    }
    public RegionExport Export()
    {
        byte[] bytes = RegionDocument.Export(Values().OrderBy(pair => pair.Key).Select(pair => pair.Value));
        return new("regions.json", Revision, Encoding.UTF8.GetString(bytes));
    }
    public void Heartbeat() => leases.RenewSession(owner);
    public void Dispose() { foreach (var lease in claims.Values) leases.Release(lease); claims.Clear(); drafts.Clear(); }
}
