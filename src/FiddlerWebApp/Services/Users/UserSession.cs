using System.Security.Claims;

namespace FiddlerWebApp.Services.Users;

public sealed record WorkspaceUser(string Id, string Name, string? Email, string Provider);
public sealed record ActiveUser(string SessionId, WorkspaceUser User, DateTimeOffset LastSeen, string LoginId);

public sealed class UserDirectory(TimeProvider clock)
{
    public event Action<string>? LoginRevoked;
    private readonly object gate = new();
    private readonly Dictionary<string, ActiveUser> active = [];
    private readonly Dictionary<string, DateTimeOffset> revoked = [];
    public void Touch(string sessionId, WorkspaceUser user, string loginId)
    { lock (gate) active[sessionId] = new(sessionId, user, clock.GetUtcNow(), loginId); }
    public void Remove(string sessionId) { lock (gate) active.Remove(sessionId); }
    public IReadOnlyList<ActiveUser> Active()
    {
        lock (gate)
        {
            var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(2);
            foreach (var id in active.Where(p => p.Value.LastSeen < cutoff).Select(p => p.Key).ToArray()) active.Remove(id);
            return active.Values.OrderBy(p => p.User.Name).ToArray();
        }
    }
    public bool IsRevoked(string loginId)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var id in revoked.Where(p => p.Value < now).Select(p => p.Key).ToArray()) revoked.Remove(id);
            return revoked.ContainsKey(loginId);
        }
    }
    public IReadOnlyList<string> Revoke(string loginId, DateTimeOffset expires)
    {
        string[] sessions;
        lock (gate)
        {
            revoked[loginId] = expires;
            sessions = active.Values.Where(p => p.LoginId == loginId).Select(p => p.SessionId).ToArray();
            foreach (string session in sessions) active.Remove(session);
        }
        LoginRevoked?.Invoke(loginId);
        return sessions;
    }
}

/// <summary>Identity is established by the server authentication ticket, never by a page field.</summary>
public sealed class UserSession(UserDirectory users, ResourceLeaseManager leases, TimeProvider clock) : IDisposable
{
    public event Action? IdentityChanged;
    private bool disposed;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public WorkspaceUser? User { get; private set; }
    public string LoginId { get; private set; } = "";
    private DateTimeOffset expires;
    public bool ConnectionActive { get; private set; } = true;
    public bool IsSignedIn => !disposed && User is not null && expires > clock.GetUtcNow() && !users.IsRevoked(LoginId);
    public void Initialize(ClaimsPrincipal principal)
    {
        if (User is not null) return;
        if (principal.Identity?.IsAuthenticated != true) return;
        string? id = principal.FindFirstValue("fiddler:user"), login = principal.FindFirstValue("fiddler:login");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(login) ||
            !long.TryParse(principal.FindFirstValue("fiddler:expires"), out long seconds)) return;
        expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        LoginId = login;
        User = new(id, principal.Identity.Name ?? "User", principal.FindFirstValue(ClaimTypes.Email), principal.FindFirstValue("fiddler:provider") ?? "Unknown");
        if (!IsSignedIn) { User = null; return; }
        users.LoginRevoked += OnRevoked;
        Heartbeat();
    }
    public WorkspaceUser RequireUser() => IsSignedIn ? User! : throw new InvalidOperationException("Sign in to use this workspace.");
    public void Heartbeat()
    {
        if (!ConnectionActive) return;
        var user = RequireUser();
        users.Touch(SessionId, user, LoginId);
        leases.RenewSession(SessionId);
    }
    public void ConnectionChanged(bool connected) { ConnectionActive = connected; if (connected && IsSignedIn) Heartbeat(); }
    private void OnRevoked(string loginId)
    {
        if (loginId != LoginId || disposed) return;
        leases.ReleaseSession(SessionId); users.Remove(SessionId);
        IdentityChanged?.Invoke();
    }
    public void Dispose() { disposed = true; users.LoginRevoked -= OnRevoked; leases.ReleaseSession(SessionId); users.Remove(SessionId); }
}
