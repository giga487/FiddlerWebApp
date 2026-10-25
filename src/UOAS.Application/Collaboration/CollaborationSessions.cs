using System.Security.Cryptography;
using FiddlerWebApp.Services.Users;
using UOAS.Contracts;

namespace UOAS.Application.Collaboration;

public sealed class CollaborationSessions(TimeProvider clock, SharedArtStore art, SharedGumpStore gumps, ResourceLeaseManager leases, SharedDocumentStore documents) : IDisposable
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    public sealed class Session(string token, string name, DateTimeOffset now)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Token { get; } = token;
        public string Name { get; } = name;
        public DateTimeOffset LastSeen = now;
        public readonly SemaphoreSlim Gate = new(1);
        public readonly Dictionary<Guid, AssetWorkspace> Workspaces = [];
        public readonly Dictionary<Guid, UOAS.Application.Regions.RegionWorkspace> RegionWorkspaces = [];
        internal int Uses;
        internal bool Closed;
        public CollaborationSession Info() => new(Token, Name, LastSeen + IdleLifetime);
        public AssetWorkspace Workspace(Guid id) => Workspaces.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Workspace not found in this session.");
        public void DisposeWorkspaces() { foreach (var workspace in Workspaces.Values) workspace.Dispose(); Workspaces.Clear(); foreach (var workspace in RegionWorkspaces.Values) workspace.Dispose(); RegionWorkspaces.Clear(); }
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private bool disposed;
    public CollaborationSession Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length < 2 || name.Length > 60 || name.Any(char.IsControl)) throw new ArgumentException("Enter a display name from 2 to 60 characters.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Sweep();
            if (sessions.Count >= 64) throw new InvalidOperationException("Too many active collaboration sessions. Close a session or wait for idle expiry.");
            var session = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), name, clock.GetUtcNow());
            sessions.Add(session.Token, session); return session.Info();
        }
    }
    public async Task<Use> AcquireAsync(string token, CancellationToken cancellationToken)
    {
        Session session;
        lock (gate)
        {
            Sweep();
            if (disposed || !sessions.TryGetValue(token, out session!)) throw new KeyNotFoundException("Collaboration session expired. Start a new session.");
            session.Uses++;
        }
        try { await session.Gate.WaitAsync(cancellationToken); }
        catch { Release(session, acquired: false); throw; }
        if (session.Closed) { Release(session); throw new KeyNotFoundException("Collaboration session was closed."); }
        lock (gate) session.LastSeen = clock.GetUtcNow();
        foreach (var workspace in session.Workspaces.Values) workspace.Heartbeat();
        foreach (var workspace in session.RegionWorkspaces.Values) workspace.Heartbeat();
        return new(this, session);
    }
    public AssetWorkspace Open(Session session, string directory)
    {
        if (session.Workspaces.Count >= 4) throw new InvalidOperationException("Close an existing workspace before opening another.");
        var workspace = new AssetWorkspace(directory, session.Id, new WorkspaceUser(session.Id, session.Name, null, "Collaboration"), art, gumps, leases, documents);
        session.Workspaces.Add(workspace.Id, workspace); return workspace;
    }
    public void Close(Session session)
    { lock (gate) { session.Closed = true; sessions.Remove(session.Token); if (session.Uses == 0) session.DisposeWorkspaces(); } }
    public void Sweep()
    { lock (gate) foreach (var session in sessions.Values.Where(session => session.Uses == 0 && session.LastSeen + IdleLifetime <= clock.GetUtcNow()).ToArray()) Close(session); }
    private void Release(Session session, bool acquired = true)
    {
        if (acquired) session.Gate.Release();
        lock (gate) { session.Uses--; if (session.Closed && session.Uses == 0) session.DisposeWorkspaces(); }
    }
    public void Dispose()
    { lock (gate) { disposed = true; foreach (var session in sessions.Values.ToArray()) Close(session); } }
    public sealed class Use(CollaborationSessions owner, Session session) : IDisposable
    {
        public Session Session => session;
        public void Dispose() => owner.Release(session);
    }
}

public sealed class CollaborationReaper(CollaborationSessions sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) sessions.Sweep(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
