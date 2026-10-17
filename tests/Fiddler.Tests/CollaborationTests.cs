using System.Security.Claims;
using Fiddler.Formats;
using Fiddler.Formats.Gumps;
using FiddlerWebApp.Services;
using FiddlerWebApp.Services.Users;
using Xunit;

namespace Fiddler.Tests;

public sealed class CollaborationTests
{
    private static readonly WorkspaceUser Alice = new("alice", "Alice", null, "Test"), Bob = new("bob", "Bob", null, "Test");
    private static readonly AssetKey Key = new("archive", "Gump", 44);
    private const int Red = unchecked((int)0xFFFF0000), Green = unchecked((int)0xFF00FF00), Blue = unchecked((int)0xFF0000FF);
    private static GumpRecord Solid(int color) => GumpEncoder.Encode(DecodedImage.FromArgb(1, 1, [color]));

    [Fact]
    public async Task ConcurrentAcquisitionAllowsOnlyOneSessionAndScopesByArchiveAndType()
    {
        var leases = new ResourceLeaseManager(TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            try { return leases.Acquire(Key, i.ToString(), Alice); }
            catch (InvalidOperationException) { return null; }
        })));
        Assert.Single(results.OfType<EditLease>());
        Assert.NotNull(leases.Acquire(Key with { Archive = "other" }, "bob", Bob));
        Assert.NotNull(leases.Acquire(Key with { Kind = "Art" }, "bob", Bob));
    }

    [Fact]
    public void ExpiredTokensCannotWriteReleaseOrRenewNewOwners()
    {
        var clock = new TestClock(); var leases = new ResourceLeaseManager(clock);
        var old = leases.Acquire(Key, "one", Alice);
        clock.Advance(TimeSpan.FromMinutes(3));
        var current = leases.Acquire(Key, "two", Bob);
        Assert.Throws<InvalidOperationException>(() => leases.ExecuteOwned(old, () => throw new Exception("Must not run")));
        leases.Release(old); leases.RenewSession("one");
        Assert.Equal(current.Token, Assert.Single(leases.List()).Token);
    }

    [Fact]
    public void SameUserInAnotherTabStillNeedsItsOwnLeaseAndPinnedSavesCannotBeStolen()
    {
        var clock = new TestClock(); var leases = new ResourceLeaseManager(clock);
        var lease = leases.Acquire(Key, "one", Alice);
        Assert.Equal(lease.Token, leases.Acquire(Key, "one", Alice).Token);
        Assert.Throws<InvalidOperationException>(() => leases.Acquire(Key, "two", Alice));
        using (leases.Pin([lease]))
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Throws<InvalidOperationException>(() => leases.Acquire(Key, "two", Bob));
            leases.ReleaseSession("one");
            Assert.True(Assert.Single(leases.List()).Saving);
        }
        Assert.Empty(leases.List());
    }

    [Fact]
    public void IdentityMustComeFromAuthenticatedTicketAndRevocationStopsAnExistingCircuit()
    {
        var clock = new TestClock(); var leases = new ResourceLeaseManager(clock); var users = new UserDirectory(clock);
        using var session = new UserSession(users, leases, clock);
        session.Initialize(new ClaimsPrincipal(new ClaimsIdentity([new Claim("fiddler:user", "alice")])));
        Assert.False(session.IsSignedIn);
        Assert.Throws<InvalidOperationException>(() => session.RequireUser());
        session.Initialize(Principal(Alice, clock));
        Assert.Equal("Alice", session.RequireUser().Name);
        var lease = leases.Acquire(Key, session.SessionId, Alice);
        session.ConnectionChanged(false);
        clock.Advance(TimeSpan.FromMinutes(3)); session.Heartbeat();
        Assert.Empty(leases.List());
        users.Revoke(session.LoginId, clock.GetUtcNow().AddHours(8));
        Assert.Throws<InvalidOperationException>(() => session.RequireUser());
    }

    [Fact]
    public async Task TwoUsersPublishDifferentIdsWithoutLosingEitherChangeAndRestartRestoresLatest()
    {
        using var env = new Environment();
        using var alice = env.Workspace(Alice); using var bob = env.Workspace(Bob);
        alice.Open(env.Source); bob.Open(env.Source);
        alice.BeginEdit(1); bob.BeginEdit(2);
        Assert.Throws<InvalidOperationException>(() => bob.BeginEdit(1));
        Assert.Equal(Red, bob.Gumps!.Read(1).Pixels.Span[0]); // Reading stays available.
        alice.PutGump(1, Solid(Green)); bob.PutGump(2, Solid(Blue));
        Assert.Equal(Red, bob.Gumps.Read(1).Pixels.Span[0]); // Alice's draft is private.
        var saved = await Task.WhenAll(alice.SaveSharedAsync(env.Output, default), bob.SaveSharedAsync(env.Output, default));
        Assert.Equal([1L, 2L], saved.Select(s => s.Revision).Order());
        var restarted = new SharedGumpStore(env.State, env.Leases);
        var snapshot = restarted.Open(env.Source);
        using (snapshot.Document)
        {
            Assert.Equal(2, snapshot.Revision);
            Assert.Equal(Green, snapshot.Document.Read(1).Pixels.Span[0]);
            Assert.Equal(Blue, snapshot.Document.Read(2).Pixels.Span[0]);
            Assert.False(snapshot.Document.HasChanges);
        }
        using var original = GumpDocument.Open(env.Source);
        Assert.Equal(Red, original.Read(1).Pixels.Span[0]);
        Assert.Equal(Red, original.Read(2).Pixels.Span[0]);
        Assert.Empty(env.Leases.List());
    }

    [Fact]
    public async Task StaleDraftCannotOverwriteAReassignedIdAndSurvivesRefresh()
    {
        using var env = new Environment();
        using var alice = env.Workspace(Alice); using var bob = env.Workspace(Bob);
        alice.Open(env.Source); bob.Open(env.Source);
        alice.BeginEdit(1); alice.PutGump(1, Solid(Green));
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        bob.BeginEdit(1); bob.PutGump(1, Solid(Blue)); await bob.SaveSharedAsync(env.Output, default);
        alice.RefreshShared();
        Assert.Equal(Green, alice.Gumps!.Read(1).Pixels.Span[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.SaveSharedAsync(env.Output, default));
        Assert.Throws<InvalidOperationException>(() => alice.BeginEdit(1));
        Assert.True(alice.Gumps.HasChanges);
        alice.UndoGump(1); alice.RefreshShared(); alice.BeginEdit(1);
        Assert.Equal(Blue, alice.Gumps.Read(1).Pixels.Span[0]);
    }

    [Fact]
    public async Task ExpiredUnmodifiedReservationCanFollowANewerPublishedVersion()
    {
        using var env = new Environment();
        using var alice = env.Workspace(Alice); using var bob = env.Workspace(Bob);
        alice.Open(env.Source); bob.Open(env.Source); alice.BeginEdit(1);
        env.Clock.Advance(TimeSpan.FromMinutes(3));
        bob.BeginEdit(1); bob.PutGump(1, Solid(Blue)); await bob.SaveSharedAsync(env.Output, default);
        alice.BeginEdit(1);
        Assert.True(alice.OwnsEdit(1));
        Assert.Equal(Blue, alice.Gumps!.Read(1).Pixels.Span[0]);
    }

    [Fact]
    public async Task CanceledSaveDoesNotPublishAndUndoAndDisposalReleaseReservations()
    {
        using var env = new Environment();
        using var alice = env.Workspace(Alice); using var bob = env.Workspace(Bob);
        alice.Open(env.Source); bob.Open(env.Source);
        Assert.Throws<InvalidOperationException>(() => alice.PutGump(1, Solid(Green)));
        alice.BeginEdit(1); alice.PutGump(1, Solid(Green));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => alice.SaveSharedAsync(env.Output, new CancellationToken(true)));
        Assert.True(alice.Gumps!.HasChanges); Assert.True(alice.OwnsEdit(1));
        var basis = env.Shared.Open(env.Source);
        using (basis.Document) Assert.Equal(0, basis.Revision);
        alice.UndoGump(1); bob.BeginEdit(1); bob.Dispose();
        Assert.Empty(env.Leases.List());
    }

    [Fact]
    public void RevokedUserCannotBeginOrMutateAnExistingDraft()
    {
        using var env = new Environment();
        using var user = env.Session(Alice);
        using var workspace = new MulWorkspace(user, env.Shared, env.Leases);
        workspace.Open(env.Source); workspace.BeginEdit(1);
        int notifications = 0; user.IdentityChanged += () => notifications++;
        env.Users.Revoke(user.LoginId, env.Clock.GetUtcNow().AddHours(8));
        Assert.Throws<InvalidOperationException>(() => workspace.PutGump(1, Solid(Green)));
        Assert.Throws<InvalidOperationException>(() => workspace.BeginEdit(2));
        Assert.False(workspace.Gumps!.HasChanges);
        Assert.Equal(1, notifications);
        Assert.Empty(env.Leases.List());
        Assert.Empty(env.Users.Active());
    }

    [Fact]
    public async Task ManifestFailureReportsCompletedExportAndPreservesSharedRevisionAndDrafts()
    {
        using var env = new Environment();
        File.WriteAllText(env.State, "Not a directory");
        using var alice = env.Workspace(Alice);
        alice.Open(env.Source); alice.BeginEdit(1); alice.PutGump(1, Solid(Green));
        var error = await Assert.ThrowsAsync<IOException>(() => alice.SaveSharedAsync(env.Output, default));
        Assert.Contains("The export is complete at", error.Message);
        Assert.True(alice.Gumps!.HasChanges);
        var snapshot = env.Shared.Open(env.Source);
        using (snapshot.Document) Assert.Equal(0, snapshot.Revision);
        var directory = Assert.Single(Directory.GetDirectories(env.Output));
        using var result = GumpDocument.Open(directory);
        Assert.Equal(Green, result.Read(1).Pixels.Span[0]);
    }

    private static ClaimsPrincipal Principal(WorkspaceUser user, TimeProvider clock) => new(new ClaimsIdentity([
        new(ClaimTypes.Name, user.Name), new("fiddler:user", user.Id), new("fiddler:provider", user.Provider),
        new("fiddler:login", Guid.NewGuid().ToString("N")), new("fiddler:expires", clock.GetUtcNow().AddHours(8).ToUnixTimeSeconds().ToString())], "Test"));
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }
    private sealed class Environment : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("fiddler-collaboration-").FullName;
        private readonly List<UserSession> sessions = [];
        public string Source => Path.Combine(root, "source");
        public string Output => Path.Combine(root, "exports");
        public string State => Path.Combine(root, "state");
        public TestClock Clock { get; } = new();
        public ResourceLeaseManager Leases { get; }
        public UserDirectory Users { get; }
        public SharedGumpStore Shared { get; }
        public Environment()
        {
            Leases = new(Clock); Users = new(Clock); Shared = new(State, Leases);
            GumpEditingTests.WriteMul(Source, new() { [1] = Solid(Red), [2] = Solid(Red) });
        }
        public UserSession Session(WorkspaceUser profile)
        { var user = new UserSession(Users, Leases, Clock); user.Initialize(Principal(profile, Clock)); sessions.Add(user); return user; }
        public MulWorkspace Workspace(WorkspaceUser profile) => new(Session(profile), Shared, Leases);
        public void Dispose() { foreach (var user in sessions) user.Dispose(); Directory.Delete(root, true); }
    }
}
