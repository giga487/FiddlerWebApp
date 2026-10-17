using System.Security.Claims;
using FiddlerWebApp.Services;
using FiddlerWebApp.Services.Users;

namespace FiddlerWebApp.Api;

public sealed record CreateWorkspaceRequest(string? Directory = null);
public sealed record WorkspaceInfo(Guid Id, string Directory, long Version, long SharedRevision, bool HasGumps, int DraftCount, DateTimeOffset ExpiresAt);

/// <summary>A request-independent workspace. All access is serialized by the registry.</summary>
public sealed class ApiWorkspace : IDisposable
{
    private readonly IServiceScope scope;
    public Guid Id { get; } = Guid.NewGuid();
    public string Directory { get; }
    public UserSession User { get; }
    public MulWorkspace Assets { get; }
    public WorldAssetWorkspace World => scope.ServiceProvider.GetRequiredService<WorldAssetWorkspace>();
    public AnimationWorkspace Animations => scope.ServiceProvider.GetRequiredService<AnimationWorkspace>();
    public long Version { get; internal set; } = 1;
    public string ETag => $"\"{Version}\"";
    public DateTimeOffset LastAccess { get; internal set; }
    internal readonly SemaphoreSlim Gate = new(1);
    internal int Uses;
    internal bool Closed;
    public ApiWorkspace(IServiceScope scope, ClaimsPrincipal principal, string? directory, DateTimeOffset now)
    {
        this.scope = scope;
        User = scope.ServiceProvider.GetRequiredService<UserSession>();
        User.Initialize(principal); User.RequireUser();
        directory ??= scope.ServiceProvider.GetRequiredService<ClientDirectorySettings>().Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("Enter an absolute server directory or configure your data folder first.");
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        if (!folder.Exists) throw new DirectoryNotFoundException("The workspace directory does not exist.");
        Directory = folder.FullName;
        var names = folder.EnumerateFiles().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assets = scope.ServiceProvider.GetRequiredService<MulWorkspace>();
        if (names.Contains("art.mul") && names.Contains("artidx.mul") || names.Contains("gumpart.mul") && names.Contains("gumpidx.mul") || names.Contains("gumpartlegacymul.uop")) Assets.Open(Directory);
        LastAccess = now;
    }
    public WorkspaceInfo Info() => new(Id, Directory, Version, Assets.SharedRevision, Assets.Gumps is not null, Assets.Gumps?.DirtyCount ?? 0, LastAccess + ApiWorkspaceRegistry.IdleLifetime);
    public void Dispose() => scope.Dispose();
}

/// <summary>Owned scopes keep readers and drafts alive between HTTP requests, without holding a Blazor circuit.</summary>
public sealed class ApiWorkspaceRegistry(IServiceScopeFactory scopes, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private readonly Dictionary<Guid, ApiWorkspace> workspaces = [];
    private bool disposed;
    public ApiWorkspace Create(ClaimsPrincipal principal, string? directory)
    {
        Sweep();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string userId = principal.FindFirstValue("fiddler:user") ?? throw new ApiException(401, "A signed-in user context is required.");
            if (workspaces.Count >= 64 || workspaces.Values.Count(w => w.User.User?.Id == userId) >= 8)
                throw new ApiException(429, "Too many open API workspaces. Close an existing workspace or wait for idle expiry.");
            var scope = scopes.CreateScope();
            try
            {
                var workspace = new ApiWorkspace(scope, principal, directory, clock.GetUtcNow());
                workspace.Uses = 1; // The create response owns this initial use.
                workspaces.Add(workspace.Id, workspace);
                return workspace;
            }
            catch { scope.Dispose(); throw; }
        }
    }
    private static bool Owns(ApiWorkspace workspace, ClaimsPrincipal principal) =>
        workspace.User.User?.Id == principal.FindFirstValue("fiddler:user") && workspace.User.LoginId == principal.FindFirstValue("fiddler:login");
    public IReadOnlyList<WorkspaceInfo> List(ClaimsPrincipal principal)
    {
        Sweep();
        lock (gate) return workspaces.Values.Where(w => !w.Closed && Owns(w, principal)).Select(w => w.Info()).ToArray();
    }
    public async Task<WorkspaceUse> AcquireAsync(Guid id, ClaimsPrincipal principal, CancellationToken token)
    {
        ApiWorkspace workspace;
        lock (gate)
        {
            if (disposed || !workspaces.TryGetValue(id, out workspace!) || workspace.Closed || !Owns(workspace, principal)) throw new ApiException(404, "API workspace not found.");
            if (!workspace.User.IsSignedIn || workspace.Uses == 0 && workspace.LastAccess + IdleLifetime <= clock.GetUtcNow())
            { Close(workspace); throw new ApiException(404, "API workspace expired. Open a new workspace."); }
            workspace.Uses++;
        }
        try { await workspace.Gate.WaitAsync(token); }
        catch { ReleaseUse(workspace); throw; }
        try
        {
            if (workspace.Closed || !workspace.User.IsSignedIn) throw new ApiException(404, "API workspace is no longer active.");
            workspace.LastAccess = clock.GetUtcNow();
            workspace.User.Heartbeat();
            return new WorkspaceUse(this, workspace);
        }
        catch { workspace.Gate.Release(); ReleaseUse(workspace); throw; }
    }
    public void Close(ApiWorkspace workspace)
    {
        lock (gate)
        {
            if (workspace.Closed) return;
            workspace.Closed = true; workspaces.Remove(workspace.Id);
            if (workspace.Uses == 0) workspace.Dispose();
        }
    }
    public void ReleaseUse(ApiWorkspace workspace)
    {
        lock (gate)
        {
            workspace.Uses--;
            if (workspace.Closed && workspace.Uses == 0) workspace.Dispose();
        }
    }
    public void Sweep()
    {
        lock (gate)
            foreach (var workspace in workspaces.Values.Where(w => !w.User.IsSignedIn || w.Uses == 0 && w.LastAccess + IdleLifetime <= clock.GetUtcNow()).ToArray()) Close(workspace);
    }
    public void Dispose()
    { lock (gate) { disposed = true; foreach (var workspace in workspaces.Values.ToArray()) Close(workspace); } }
    public sealed class WorkspaceUse(ApiWorkspaceRegistry registry, ApiWorkspace workspace) : IAsyncDisposable
    {
        public ApiWorkspace Workspace => workspace;
        public ValueTask DisposeAsync() { workspace.Gate.Release(); registry.ReleaseUse(workspace); return ValueTask.CompletedTask; }
    }
}

public sealed class ApiWorkspaceReaper(ApiWorkspaceRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) registry.Sweep(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
