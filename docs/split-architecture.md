# Backend, WASM and per-ID editing

## Projects

| Project | Responsibility |
| --- | --- |
| UOAS.Contracts | Network DTOs and ApplicationInfo constants; no file access or graphics dependency |
| UOAS.Presentation | UI-independent observable ViewModels, asynchronous commands and HTTP transport |
| UOAS.Frontend | Standalone Blazor WebAssembly views and SkiaSharp canvases |
| UOAS.Application | Session workspaces, archive readers, lease coordinator and versioned publishers |
| UOAS.Backend | ASP.NET Core session/API host; no Razor UI or Interactive Server circuit |
| Fiddler.Formats / Fiddler.Graphics | Existing validated formats and server renderers |

The source namespaces of existing libraries remain stable so reusable service
tests and the plugin SDK continue to compile. The WASM project references only the
presentation layer and contracts, plus SkiaSharp.Views.Blazor. It never downloads
the backend's source archives or application services. The Interactive Server
host and its solution, pages, login hooks and old API routes have been removed.

## Session and resource lifetime

`POST /api/sessions` creates a random, tab-specific session capability for a
display name. Further calls require `X-UOAS-Session`; no password or verified
identity is involved. Tokens stay in application memory and never appear in
image URLs. The API uses explicit session headers and configured CORS origins.
No cookie-based authentication or alternate API host is installed.

Each session owns up to four workspaces. Requests within a session are serialized
so readers and mutable drafts are not used concurrently. Different sessions
can operate independently. Session expiry waits for in-flight operations before
disposing readers. Each workspace has a separate lease owner, so opening the
same ID in another tab or workspace does not inherit the first workspace's lock.

The asset key includes the normalized library path, collection and ID. For Art,
Land 10 and Item 10 are different slots; Gump 10 is also independent. Document
keys also include the filename and record kind, so font/localization variants do
not collide. Directory aliases and symlinked directory segments normalize to a
shared key; Windows path case differences do not bypass the coordinator.

Acquisition is atomic. Writes validate the server-held lease token. Publishing
pins all relevant leases, checks the per-record base versions and merges drafts
against the latest published file. A stale lease cannot release or mutate the
new owner's resource. Published files and manifests live under backend App_Data;
the source library remains unchanged. Configure and back up that state together.
One backend process owns the coordinator; multi-replica deployment needs a
distributed coordinator and shared version store before it can be supported.

The UI polls lock metadata every three seconds. These polls renew workspace
leases. No image decoding or download occurs during lock-only polling. Lost
connectivity disables client-side write controls until lock status is known
again; server validation remains authoritative. Tab closure is not guaranteed to
run cleanup, so disconnected leases expire after two minutes. Explicit **End
session** disposes workspaces and releases their locks immediately.

## API surface

| Route group | Purpose |
| --- | --- |
| /api/sessions and /api/session | Start, inspect and end a collaboration session |
| /api/libraries | List backend-configured library IDs and names |
| /api/workspaces | Open and close session-owned library readers |
| .../assets/{kind} | Paginated Gump/Item/Land metadata including empty slots |
| .../assets/{kind}/{id}/image | Requested PNG/thumbnail GET and locked PNG PUT |
| .../locks/{kind}/{id} | Acquire or release an asset ID; no client fencing tokens |
| .../publish/{kind} | Merge and publish Art or Gump drafts to backend-managed output |
| .../documents | Font, hue, Cliloc and TileData record editors with per-ID leases |
| .../comparison | Gump snapshot comparison, paginated differences and locked ID copy |
| .../tools | Multi previews, animation catalogs/atlases and automatic hue metadata |
| /api/plugins | Quarantined packages, operator-trusted activation and commands |
| /api/diagnostics | Filtered live backend logs and capture level control |
| /api/openapi/v1.json | Generated API description |

No endpoint accepts an arbitrary source or export directory. Library paths and
export roots are configured on the backend. Original indexed archives stay open
for the workspace and are accessed by record offset. World readers are reused
while their selection is unchanged; explicit refresh invalidates those readers.
Images returned from private workspaces use no-store response semantics.

## UI coverage

The new frontend includes the ASCII landing page, paginated graphics catalog,
PNG import, Art removal, lock owner indicators, shared Art/Gump publishing,
local Skia composition with PNG download, Multi previews, a paginated animation
catalog with local playback, file inventory and diagnostics with UTC date/cursor
filters. Individual land tiles have a dedicated entry point; full map browsing
and editing are outside the UI. Gump comparison captures a reference reader and
copies only to owned destination IDs. Fonts expose glyph lookup, offsets, PNG
replacement/export and text previews. Hues expose gradient/copy/undo tools and
a local sample board; automatic mode follows TileData PartialHue. Cliloc supports
text search and editing; TileData exposes dedicated numeric fields and named flags.

Plugin uploads remain quarantined without assembly loading. The server operator
must approve the exact package SHA256 in Plugins:TrustedPackageHashes before
activation or execution. Trust is rechecked for every invocation; commands receive
detached pixels. This is an operator trust policy, not account authentication or
a code sandbox. Packages persist but active instances do not survive restart.

## Verification

`tests/UOAS.Tests` exercises the backend through HTTP with independently named
sessions and tests the UI-independent ViewModels. It covers normalized-library
lock collisions, different-ID editing, expiry, ownership, stale publishers,
merged Art and Cliloc output, source preservation and command reentrancy.
`tests/Fiddler.Tests` retains format, rendering, logging and reusable-service
tests. Tests specific to removed server pages, login hooks and old HTTP routes
have been retired; active API/session behavior is tested in `tests/UOAS.Tests`.

`tools/browser-smoke.mjs` uses Node 24's native WebSocket client with a disposable
Chromium CDP profile. Start both hosts against synthetic fixtures, then start
headless Chromium with `--remote-debugging-port=9239` and run the script. Set
`UOAS_FRONTEND`, `UOAS_CDP` and `UOAS_SCREENSHOT_DIR` as needed. It checks two
WASM sessions, a real Skia canvas, owner visibility, disabled foreign uploads,
live lock transfer, explicit-session cleanup and mobile overflow. The script
closes its disposable browser. No client assets are bundled in tests.
