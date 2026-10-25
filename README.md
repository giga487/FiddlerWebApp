# UOAssetsService (UOAS)

Ultima Online asset workshop with an ASP.NET Core backend and a standalone
Blazor WebAssembly frontend. ViewModels and commands use MVVM; SkiaSharp renders
server previews and browser canvases. All interface text is in English.

## Run

Open **UOAS.sln**. Install .NET SDK 10 and the `wasm-tools` workload, then run
these commands in separate terminals from this repository:

```powershell
dotnet run --project src/UOAS.Backend -- --Storage:RootDirectory="D:\UOAssets"
dotnet run --project src/UOAS.Frontend
```

Open [the workshop](http://localhost:5178). The backend listens on
`http://localhost:5188`. Enter a display name, select a library and choose
**Open library**. Each browser tab starts a separate collaboration session.
Names identify designers; they are not verified accounts. Authentication and
role-based authorization are deferred.

The old Interactive Server application has been removed. There is one solution
and two runnable projects: `UOAS.Backend` and `UOAS.Frontend`.

## Architecture

```text
UOAS.Frontend      Blazor WASM views and SkiaSharp canvases
    |
UOAS.Presentation  Observable ViewModels, commands and HTTP client
    |
UOAS.Contracts     DTOs and ApplicationInfo product constants
        HTTP / JSON / requested PNGs
UOAS.Backend       ASP.NET Core API and collaboration session host
    |
UOAS.Application   Workspaces, per-ID leases, drafts and shared publishing
    |
Fiddler.Graphics   Reusable server-side SkiaSharp renderers
    |
Fiddler.Formats    Bounded MUL/IDX/UOP readers and resource encoders
```

The frontend has no reference to the backend, application services, file readers
or source archives. The backend retains handles and indices, reads requested
records and returns only paginated metadata and requested images. Zoom, pan,
composition and animation playback run locally in WASM.

See [split architecture](docs/split-architecture.md) and
[format modules](docs/architecture.md).

## Libraries and output

Configure one existing physical folder using backend `Storage:RootDirectory`.
The Configuration page browses that folder as `/`, selects asset directories and
region JSON files, and saves relative paths and an export directory. Sources and
exports must stay inside the root; symbolic links and junctions are blocked.
The root itself is only configurable in appsettings, environment or startup
arguments. Restart the backend when changing it. See [storage configuration](docs/storage-configuration.md).

Original files remain read-only. New published versions are written beneath
the selected export directory (default `Exports` inside the root).
Shared version manifests and files under backend `App_Data` must be backed up
together. Paths belong to the backend computer, not the browser computer.

Configure `BackendUrl` in frontend `wwwroot/appsettings.json`, backend
`Frontend:Origins` and `AllowedHosts` when changing hosts or ports.

## Per-ID collaboration

Selecting or viewing an asset does not lock it. **Start editing ID** acquires a
lease for the normalized library, resource type and ID. Other designers can
view that ID, see **Locked by <name>**, and cannot modify it. Land 10, Item 10
and Gump 10 are independent resources. Font, hue, Cliloc and TileData records
also enforce per-record locks. Source filename variants remain independent.

The backend validates ownership on every write and publish. Client-side button
states are only presentation. Writes and exports use server-held fencing tokens
and per-record versions; an expired editor cannot overwrite a newer version.
Changes to different IDs merge when published. Private drafts are not visible
to other designers until publication.

Lock status updates every three seconds. Leases expire after two minutes without
renewal. Explicit release or **End session** releases locks immediately. Browser
closure and network failure rely on expiry. Sessions expire after 15 minutes
of inactivity. Run one backend instance per library: the current coordinator
is in memory and does not support multiple API replicas.

See [sessions and collaboration](docs/users-and-collaboration.md).

## Tools

The home page summarizes the workshop beneath an animated Ultima Online ASCII
banner. Animation can be paused and respects reduced-motion preferences.

- **Graphics**: paginated Gump/Item/Land catalog with empty slots, ID search,
  PNG previews/import, Art removal, private drafts and shared version publishing.
- **Compose**: local SkiaSharp Gump composition, dragging and PNG download.
- **Land tiles**: individual terrain graphics, empty slots, PNG editing and ID locks;
  full map browsing and editing are outside this application's UI.
- **Multis and animations**: Multi previews, paginated animation bodies, category/ID
  search, available actions/directions, local playback, frame seek, loop and zoom.
- **Compare Gumps**: compare configured libraries, filter differences, inspect both
  images and copy reference records into locked destination IDs.
- **Resources**: font PNG replacement, 32-sample hue palettes, Cliloc text and
  TileData fields and named flags, with per-record locks and versioned publishing.
  Fonts include character lookup, offsets, text preview and glyph PNG export.
  Hues include gradients, palette undo/copy and a local Skia sample board with
  full, gray-only and TileData-driven automatic coloring.
- **Inventory**: available/missing client files and basic index checks.
- **Configuration**: backend folder browser confined to one root, editable source
  lists and export path, persistent revisions and conflict detection.
- **Diagnostics**: filtered backend logs, exceptions, structured properties and
  UTC time ranges, view clearing and runtime capture verbosity, alongside console
  and rotating JSON Lines files.
- **Plugins**: DLL/ZIP quarantine, SHA256 approval through server configuration,
  activation/deactivation, removal and commands on detached selected images.
- **Server regions**: configured ModernUO regions.json sources, volume/Z preview,
  hierarchy, type-specific rules, named ID locks and merged JSON publishing.
  See [server region editor](docs/server-regions.md) for configuration and deployment.
- **API reference**: generated OpenAPI at backend `/api/openapi/v1.json`.

Plugin uploads never approve themselves. Add an inspected package's SHA256 to
`Plugins:TrustedPackageHashes` on the backend before activating it. Activation
executes code with the backend process's permissions; it is not a sandbox.
See [plugin packages](docs/plugins.md).

Animation UOP, verdata and diff patches are not implemented. Multis
are read-only previews. Art UOP reads are available for Multi scenes; editable
Art currently uses MUL/IDX. See [world formats](docs/world-assets.md),
[animation rendering](docs/animation-rendering.md) and
[resource editing](docs/client-resource-editing.md) for format details.

## Logging

`ILogger<T>` flows through Serilog to console, the bounded diagnostics sink and
`App_Data/logs/uoas-YYYYMMDD.jsonl`. Files roll daily and at 10 MiB, retaining the
latest 14 files. `Diagnostics:LogDirectory` overrides the output directory;
`Diagnostics:BufferCapacity` controls the live buffer (default 2,000; max 10,000).

Capture levels use `Serilog:MinimumLevel:Default` and category overrides under
`Serilog:MinimumLevel:Override`. Runtime capture changes affect all sinks and
sessions until restart. Display filters do not delete stored events. Restarting
clears the live buffer; file history is not loaded into the diagnostics page.

## Verification

```powershell
dotnet build UOAS.sln
dotnet test UOAS.sln
```

`tests/Fiddler.Tests` covers format libraries, reusable services and renderers.
`tests/UOAS.Tests` covers the active backend HTTP API, locks, shared publishing
and MVVM commands. `tools/browser-smoke.mjs` checks two real WASM sessions and
Skia rendering through a disposable Chromium CDP profile. Fixtures are synthetic;
no game assets are bundled in tests.
