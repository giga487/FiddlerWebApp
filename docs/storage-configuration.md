# Storage root and application configuration

An operator grants UOAS access to one existing physical directory:

```json
{ "Storage": { "RootDirectory": "D:/UOAssets" } }
```

Set this in backend appsettings or the Git-ignored appsettings.Local.json.
Environment variables and command-line arguments take precedence. The root is
fixed for the running backend and cannot be changed by any configuration API.
Restart the backend after changing it. A new root has its own saved settings.

## Folder browser

Open `/configuration` after entering a display name. The browser shows the root
as `/`, lists one directory at a time and supports parent navigation and paging.
It returns names, entry kind and file sizes. It does not expose a generic file
download, deletion, rename or movement endpoint.

Select a folder and choose Use folder for assets, Use folder for regions.json,
or Use folder for exports. Individual JSON files have a Use region file action.
Edit the source ID and display name, remove unwanted source registrations and
choose Save configuration. Removing a registration does not delete its files.
Region JSON is validated before settings are saved. Sources are refreshed when
new asset or region workspaces are opened; active workspaces keep their drafts.

Settings contain only relative paths, for example:

```json
{
  "revision": 1,
  "libraries": [{ "id": "classic", "name": "Classic", "directory": "Clients/Classic" }],
  "regionSources": [{ "id": "server", "name": "Server", "file": "ServerData/regions.json", "maps": [], "types": [] }],
  "exportDirectory": "Exports"
}
```

Settings persist outside the root browser in backend
`App_Data/workspace-settings/<root-hash>.json`; `.bak` preserves the preceding
saved revision. Writes use a temporary file and atomic replacement. A stale
revision receives 409 instead of overwriting another designer's configuration.
Save operations are recorded in backend diagnostics with the session display name.

Existing Assets:Libraries, Regions:Sources and Assets:ExportDirectory settings
seed the initial configuration only when no saved settings exist. Seed paths can
be relative or absolute paths within the root. An outside seed is rejected.
Saved application settings then take precedence over these seed entries.

## Boundary enforcement

The backend rejects rooted paths, drive/UNC paths, alternate data stream syntax,
parent traversal, ambiguous trailing dots/spaces and invalid path segments.
Existing path components are checked for reparse points; links and junctions are
blocked even if they currently point inside the root. Library top-level entries
are also checked before native readers open them. Region files and publication
destinations use the same resolver; there is no alternate legacy path API.

Each directory browse returns at most 100 entries per request (50 in the UI),
with a 10,000-entry directory limit. Configuration supports up to 32 asset
libraries and 32 region sources. Exports may create a new relative output folder.
The backend service account should have OS access appropriate to the configured
root. Filesystem changes made directly by an OS administrator are outside the
application's browser operations.

The current display-name collaboration sessions can access Configuration.
Authentication and role-based permissions remain deferred; no administrator
identity is inferred from a chosen name. Future authorization policies can guard
the configuration and browse endpoints while retaining this root boundary.

## API

All routes use the existing X-UOAS-Session header:

- GET `/api/storage`: whether an operator configured a root.
- GET `/api/storage/entries?path=ServerData&offset=0&limit=50`: relative folder listing.
- GET `/api/configuration`: current settings and revision.
- PUT `/api/configuration`: validate and save settings with the expected revision.

RootDirectory is not accepted by the settings DTO. Unrecognized top-level
properties are rejected. The API never accepts an absolute path from the UI.
