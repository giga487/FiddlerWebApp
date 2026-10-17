namespace FiddlerWebApp.Services.Users;

public sealed record AssetKey(string Archive, string Kind, int Id);
public sealed record EditLease(AssetKey Asset, string SessionId, string UserId, string UserName, Guid Token, DateTimeOffset Expires, bool Saving = false);

/// <summary>Atomic edit leases shared by every circuit in one server process, with fencing tokens.</summary>
public sealed class ResourceLeaseManager(TimeProvider timeProvider)
{
    private readonly TimeProvider clock = timeProvider;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private sealed class Held(EditLease lease) { public EditLease Lease = lease; public int Pins; public bool ReleasePending; }
    private readonly object gate = new();
    private readonly Dictionary<AssetKey, Held> held = [];
    public EditLease Acquire(AssetKey key, string sessionId, WorkspaceUser user)
    {
        lock (gate)
        {
            Prune();
            if (held.TryGetValue(key, out var existing))
            {
                if (existing.Lease.SessionId != sessionId) throw new InvalidOperationException($"{key.Kind} {key.Id} is being edited by {existing.Lease.UserName}. Other sessions can still view it.");
                existing.Lease = existing.Lease with { Expires = clock.GetUtcNow() + Lifetime };
                return existing.Lease;
            }
            var lease = new EditLease(key, sessionId, user.Id, user.Name, Guid.NewGuid(), clock.GetUtcNow() + Lifetime);
            held.Add(key, new Held(lease));
            return lease;
        }
    }
    public IReadOnlyList<EditLease> List(string? archive = null)
    { lock (gate) { Prune(); return held.Values.Select(h => h.Lease with { Saving = h.Pins > 0 }).Where(l => archive is null || l.Asset.Archive == archive).ToArray(); } }
    public void ExecuteOwned(EditLease lease, Action action)
    { lock (gate) { Require(lease); action(); } }
    public IDisposable Pin(IReadOnlyList<EditLease> leases)
    {
        lock (gate)
        {
            foreach (var lease in leases) Require(lease);
            foreach (var lease in leases) held[lease.Asset].Pins++;
            return new PinScope(this, leases);
        }
    }
    private void Require(EditLease lease)
    {
        Prune();
        if (!held.TryGetValue(lease.Asset, out var current) || current.Lease.SessionId != lease.SessionId || current.Lease.Token != lease.Token || current.ReleasePending)
            throw new InvalidOperationException($"The edit lease for {lease.Asset.Kind} {lease.Asset.Id} expired or changed. Your draft is preserved; discard it or acquire the ID again before saving.");
    }
    public void RenewSession(string sessionId)
    {
        lock (gate)
        {
            Prune();
            foreach (var current in held.Values.Where(h => h.Lease.SessionId == sessionId && !h.ReleasePending))
                current.Lease = current.Lease with { Expires = clock.GetUtcNow() + Lifetime };
        }
    }
    public void Release(EditLease lease)
    {
        lock (gate)
            if (held.TryGetValue(lease.Asset, out var current) && current.Lease.Token == lease.Token)
            { if (current.Pins > 0) current.ReleasePending = true; else held.Remove(lease.Asset); }
    }
    public void ReleaseSession(string sessionId)
    { lock (gate) foreach (var lease in held.Values.Where(h => h.Lease.SessionId == sessionId).Select(h => h.Lease).ToArray()) Release(lease); }
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in held.Where(p => p.Value.Pins == 0 && (p.Value.ReleasePending || p.Value.Lease.Expires <= now)).Select(p => p.Key).ToArray()) held.Remove(key);
    }
    private sealed class PinScope(ResourceLeaseManager manager, IReadOnlyList<EditLease> leases) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (manager.gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (var lease in leases)
                    if (manager.held.TryGetValue(lease.Asset, out var current) && current.Lease.Token == lease.Token)
                    { current.Pins--; current.Lease = current.Lease with { Expires = manager.clock.GetUtcNow() + Lifetime }; }
                manager.Prune();
            }
        }
    }
}
