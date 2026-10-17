# Users and collaborative editing

## User context

Every file workspace requires a signed-in server identity. The header displays
the current user's name and provider, with Users and Sign out actions. The Users
page lists active users, session counts and reserved IDs. It is a presence and
workspace manager, not a password or role administration console.

Google's stable subject identifies the account. Folder preferences are persisted
per account under `App_Data/users/<SHA256-account-id>/client-directory.json`.
`Mul:Directory` seeds each new user's setting. The legacy global configuration
file is not deleted or silently shared. Drafts, composition caches and open
readers remain scoped to the interactive circuit, so tabs do not mutate each
other's temporary state. Full reloads or circuit disposal can discard private
drafts; publish changes before leaving the session.

The [HTTP API](http-api.md) also supports independent workspaces owned by the
user and login. These retain private drafts across requests for up to 15 idle
minutes and use the same shared versions and edit leases as browser circuits.

The UI does not instantiate file pages for anonymous visitors. Server-side edit
methods independently validate identity and ownership; disabling a button is
not the enforcement mechanism. Google tokens are not retained. Sign-in cookies
expire after eight hours, without sliding renewal. Sign-out revokes the login
for existing circuits and immediately releases its edit leases; authenticated
callbacks check revocation before edits. Presence entries expire after two
minutes without a heartbeat.

## Authentication mode

`Authentication:Mode` selects how identity is established:

| Mode | Behavior |
|---|---|
| `Required` (default) | Visitors sign in with Google or, in Development, a development profile. |
| `Open` | Every request is authenticated as one shared user, "Local user" (`open:local`). No sign-in page, cookie or Sign out action. |

```powershell
dotnet run --project src/FiddlerWebApp -- --Authentication:Mode=Open --Mul:Directory="D:\Data\Client"
```

Open mode keeps the API, antiforgery checks and edit leases working, but every
browser and API client is the same user: folder settings are shared and the
Users page shows each tab as a session of that user. Edit leases still separate
tabs. Startup logs a warning in Open mode. Use it only on trusted hosts, since
anyone who can reach the server can browse its folders and publish changes.
Google and development-profile settings are ignored in Open mode.

## Google sign-in

Configure these values through environment variables or a server secret store:

```text
Authentication__Google__ClientId=<Google OAuth client ID>
Authentication__Google__ClientSecret=<Google OAuth client secret>
Authentication__AllowedEmails__0=<optional allowed email>
```

Register the application's public callback URL ending in `/signin-google` in
the Google OAuth client. Use the deployed host in AllowedHosts. The application
uses the ASP.NET Core Google handler and an HTTP-only application cookie, with
PKCE and antiforgery validation on sign-out. Google setup follows the
[official ASP.NET Core documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins?view=aspnetcore-10.0).

If AllowedEmails is nonempty, only listed, verified Google email addresses are
accepted. An empty list allows any Google account that completes sign-in. No
account has additional administrative privileges in this implementation.
Without Google credentials, the sign-in page explains the missing setup and
file pages remain unavailable.

For development only:

```powershell
dotnet run --project src/FiddlerWebApp -- --environment Development --Authentication:DevelopmentProfiles=true --Mul:Directory="D:\Data\Client"
```

The development form creates a fresh unverified profile for that browser's
login. It is useful for testing two users with separate browser profiles or
private windows. It is not password authentication, does not provide account
recovery, and is disabled by default. Startup rejects this flag outside the
Development environment. Google remains the configured provider for real accounts.

## Edit leases

The lock key is the canonical source directory, resource kind and numeric ID.
Paths normalize case on Windows and resolve directory links, including parent
links. Different directories, kinds or IDs can be edited independently. Current
mutation support is Gump-only; Multi, map and animation pages are read-only.

Click Start editing ID to reserve it. A server-wide atomic lease identifies
both user and circuit, with a unique fencing token. Another tab, even for the
same account, cannot write that ID. Other users can continue to read or compare
it. Private drafts are not visible to others until publication.

Leases last two minutes and renew every fifteen seconds while connected.
Disconnection stops renewal. Expired leases cannot be revived by an old token;
reacquisition is allowed only if the ID has not gained a newer shared version.
A stale draft stays in memory for explicit undo or review. Successful publication,
undo, discard, release, sign-out or circuit disposal releases the relevant leases.
An ID with a private draft cannot be released without undoing or publishing it.
Pinned publication leases cannot expire midway through an export.

## Shared publication and conflict protection

Each source archive has an immutable latest published directory, a revision
number and per-ID versions. Sessions open a snapshot of that version. Refresh
shared version adopts the latest base while preserving the session's own drafts;
it does not hide a stale draft's original version stamp.

Publication is serialized per source archive. It validates all changed IDs'
lease tokens and expected versions, opens the latest published complete archive,
then overlays only that session's changes. If Alice edits ID 1 and Bob edits ID 2,
Bob's publication includes Alice's already-published ID 1. The result is a new
complete MUL/IDX directory; original files remain untouched. On success the
session clears its drafts and adopts the shared version.

The shared manifest is written atomically under `App_Data/shared-gumps` only
after export succeeds. Cancellation or a conflict leaves the previous manifest
and drafts intact. If manifest publication fails after export, the error reports
the completed export path and no shared pointer is replaced. Do not delete
published directories while they are referenced by the manifest. Changes to
source file sizes or timestamps are detected and require review rather than
silently rebasing onto externally modified originals.

## Current scope

Coordination is for one application process. Multiple replicas or worker
processes require a shared transactional database and distributed leases; pointing
multiple independent instances at the same files is not supported. Export files
and manifests persist; user presence and leases do not survive a restart.
Existing readers keep their snapshots until refreshed. There is no automatic
real-time sharing of unpublished image edits, account role editor, per-folder
permission administration or collaborative map modification.

Tests cover concurrent acquisition, different-ID independence, expiry/fencing,
same-account tabs, pinned exports, disconnected heartbeats, revoked identities,
concurrent merged publication, restart recovery, stale drafts, cancellation and
lease cleanup. Browser validation uses explicit development profiles to verify
two-user workflows. Real Google sign-in requires deployment credentials and
cannot be validated with synthetic identities.
