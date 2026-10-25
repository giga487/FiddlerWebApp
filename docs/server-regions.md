# Server region editor

The `/regions` WASM page edits static ModernUO definitions from a configured
`Data/regions.json`. It is independent of client MUL libraries. Enter a display
name, open Regions, choose a server source and open its region catalog.

## Server behavior verified

The implementation follows the local server's RegionJsonSerializer, Region,
RegionByNameConverter, Rectangle3DConverter, RegionJsonRegistration and
VisibilityRegion. `Data/regions.json` is an array loaded at server startup.
`$type` selects a registered region class. Named regions are identified within
a map; anonymous regions are valid and several can occupy the same map. The
server uses priority and hierarchy to select regions at a location.

Each region's Area contains one or more half-open rectangular volumes. Missing
vertical coordinates follow the server converter's defaults. Parent references
contain Name and Map; the parent must be registered before its child is read.
Expansion filters affect registration. Type-specific fields include music,
entrances, rune names, logout/spawn settings, guards and 3D visibility. Visibility
radius is 0 for inheritance or 1–24; modes are Inherit, Enabled and Disabled.

House, quest and other dynamic regions are created by game code and do not belong
to this static file. The editor does not inspect or mutate the running game's
objects and does not change terrain, statics or whole-map assets.

## Configuration

Configure explicit source IDs on the backend; browsers cannot supply paths:

```json
{
  "Regions": {
    "Sources": {
      "default": {
        "Name": "Server regions",
        "File": "D:/Server/Data/regions.json",
        "Maps": ["Sea"],
        "Types": []
      }
    }
  }
}
```

Maps found in the source are offered automatically. Maps adds valid server maps
not already represented in that file; Types adds externally registered custom
region types. The built-in type choices match the local server registration list.
Empty `[]` sources are supported. Restart the backend after changing source
configuration. Export folders use Assets:ExportDirectory.

For the server checkout in this workspace, point File to its
`PrivateSeahatsServer/Distribution/Data/regions.json`. Use the deployed server's
`Data/regions.json` when that is the intended source.

## Editing and collaboration

Browse/search by map, name or type. The editor exposes name, map, type, priority,
parent, volumes, locations, music, expansion limits, common rules and visibility
settings. Its Skia preview fits all selected-region volumes and supports a Z
slice. Advanced JSON retains properties specific to custom types. Geometry
edited through controls is written in the server's x1/y1/z1/x2/y2/z2 encoding.

UOAS assigns persistent numeric IDs in its own manifest. IDs are independent of
names, map changes and file order; they are never added to server JSON. New IDs
are reserved persistently and never reused. Multiple unnamed regions stay
independent. Source aliases share locks through their normalized filesystem key.

Acquire a region before saving or deleting it. Other sessions can read it and
see the designer's name, but cannot acquire or mutate it. Leases renew with
active requests and expire after two minutes; ending a session releases them.
Deletion is a private draft until publication; select a pending deletion and use
Undo and release region to restore it. Newly created regions already hold a lock.
Refresh includes other designers' publications and keeps private drafts.

Publication validates held leases and record versions, merges changed IDs into
the latest version and validates the combined hierarchy. Named duplicates within
a map, missing parents, cycles and invalid volumes are rejected. Renaming or
deleting a parent requires updating/deleting its dependent children in the same
publication; all changed IDs must be owned. Export orders parents before children
and writes `$type` before ordinary properties for the server's polymorphic loader.
Comments and whitespace are formatted anew; property values are preserved.

Limits: 16 MiB source/export, 10,000 definitions, 512 volumes per region, 128 drafts
per catalog and 128 hierarchy levels. One backend process coordinates each source.

## Publication and deployment

Publish creates a new regions.json under backend-managed output and persists its
revision/ID manifest in App_Data/regions. It releases published locks and refreshes
the catalog. Download regions.json exports the current catalog, including saved
private drafts; edits still in form controls must first be saved as a draft.
Publication and download do not overwrite the configured source or hot-reload the
game. Deploy the published file through the server's normal release procedure and
restart the game server to load it.

The source fingerprint is checked before publishing. External changes stop
publication and preserve drafts. To adopt a changed source as a new baseline,
configure it at a separate source file path or archive the old UOAS state and
restart the backend with the replacement source. Back up exports and manifests.

## Verification

RegionTests covers session isolation, aliases, per-ID exclusion, concurrent
publication, expired owners, source preservation, parent rename/deletion,
anonymous regions, numeric-ID persistence, geometry encodings and MVVM editing.
Set UOAS_SERVER_REGIONS_AUDIT to a local regions.json path to additionally verify
its full semantic round-trip in memory without changing the source.

Set UOAS_REGIONS=1 when running tools/browser-smoke.mjs against synthetic region
fixtures. The browser check opens two WASM sessions, renders a real Skia canvas,
checks lock owner/blocked foreign writes and publishes a region revision that
the second designer can refresh.
