# UOAS backend HTTP API

UOAS.Backend hosts the active API independently of the Blazor WASM frontend.
It exposes session-owned workspaces and server-validated per-ID edit leases.
The removed Interactive Server application's cookie/CSRF API is not hosted.

## Start a collaboration session

`POST /api/sessions` with `{"name":"Alice"}` returns a random session `token`,
the display name and `expiresAt`. Subsequent requests send this capability in
`X-UOAS-Session`. Display names are not verified accounts. Tokens remain private
to their caller and are not accepted in image URLs or resource owner fields.

`GET /api/session` inspects the session; `DELETE /api/session` ends it and
releases its readers and leases. Inactivity expiry is 15 minutes. Exact frontend
origins are configured through `Frontend:Origins`; there are no login providers
or role policies in this host.

## Libraries and workspaces

`GET /api/libraries` lists configured library IDs and display names. Open one
with `POST /api/workspaces` and `{"library":"default"}`. Source and export
paths come from application settings and are resolved within Storage:RootDirectory.
The browser can save only root-relative paths through `/api/configuration`.
`/api/storage/entries` lists directories inside that root; see
[storage configuration](storage-configuration.md).

Workspace routes under `/api/workspaces/{workspaceId}` include:

- `GET` state and `DELETE ?discard=true` to explicitly discard drafts and close.
- `POST /heartbeat` to renew leases and `POST /refresh` to reload published state.
- `GET /files` for a file inventory of that configured library.
- `GET /locks` for current owner names, resource types, IDs, expiry and own status.

Workspace and document IDs are scoped to their session. Requests from another
session receive 404. Operations within a session are serialized; separate
sessions can work on independent IDs concurrently.

## Graphics

`GET /assets/{kind}?offset=0&limit=24&includeEmpty=true` lists metadata for Gump,
Item or Land slots. `id` optionally selects an exact decimal ID. Limits are
1–100 rows; empty slots are shown by default. Source archives remain on the
backend. JSON properties are camelCase; DTO enums use their numeric values,
while path parameters also accept their enum names.

`GET /assets/{kind}/{id}` returns selected metadata. `GET .../image` returns
PNG; `thumbnail=true` returns a bounded thumbnail. These endpoints do not
acquire a lock.

`POST /locks/{kind}/{id}` acquires the ID. Other sessions can continue reading
it but receive 409 on acquisition or mutation. `DELETE /locks/{kind}/{id}`
releases a clean ID; `discard=true` explicitly undoes its private draft first.
Leases expire after two minutes without renewal. The server retains fencing
tokens and checks ownership on writes and publishes.

With an owned lease, `PUT /assets/{kind}/{id}/image` accepts `image/png` up to
8 MiB. Art IDs can be removed with `DELETE /assets/{kind}/{id}`. Gump removal
is not supported by this editor.

`POST /publish/{kind}` merges the caller's Art or Gump drafts against the latest
published version, writes a new complete archive under the configured output
root and returns its directory and revision. Original client files are not
changed. Expired or stale publishers cannot overwrite a newer record.

## Other endpoints

Atlas routes under `/api/atlas` list discovered maps, thumbnails and 256-pixel
image tiles. Each image request carries the catalog revision; changed sources
return 409. Region `/overlay` routes return viewport/Z-filtered geometry and
named locks without acquiring them. See [atlas](atlas.md) for cache, scale,
custom-map configuration and image formats.

`GET /api/region-sources` lists configured server JSON sources. `POST /api/regions`
with `{ "source": "default" }` opens a session-owned catalog independent of
client assets. `/api/regions/{catalogId}` provides state and close; `/records`
provides filtered pagination and POST creation; `/records/{id}` reads, saves or
drafts deletion. `/locks/{id}` acquires/releases an ID, `/locks` lists owners,
`/refresh` and `/heartbeat` update the catalog and renew leases. POST `/publish`
merges and validates region drafts; GET `/export` downloads JSON content. Both
return a RegionExport; source paths never come from the caller. See
[server regions](server-regions.md) for hierarchy and startup behavior.

- `.../documents/files` lists editable font/hue/Cliloc/TileData files.
- `POST .../documents` opens a listed filename and document kind.
- `.../documents/{documentId}/records` provides paginated records.
- `.../documents/{documentId}/locks/{id}` acquires/releases a record lock.
- `PUT .../documents/{documentId}/records/{id}` saves a locked JSON record draft.
- `POST .../documents/{documentId}/publish` merges and exports that document.
- `POST .../comparison` captures a configured Gump reference; GET pages its
  results. `POST .../comparison/copy` copies sourceId to an owned targetId.
- `.../tools/multi` and `.../tools/animations` provide Multi previews,
  paginated body catalogs, available actions/directions and animation atlases.
- `.../tools/hue-mode/{kind}/{id}` reports automatic partial-hue metadata.
- `.../documents/{documentId}/text?font=0&text=Example` renders font samples.
- `/api/plugins` lists/uploads quarantined packages; package routes activate,
  deactivate, remove and execute operator-approved packages. See [plugins](plugins.md).
- `/api/diagnostics` filters level/category/message/exception and UTC from/until,
  afterSequence and limit. `/api/diagnostics/status` reports the sequence cursor,
  buffer capacity and capture level. Clearing a view only advances its cursor.
- `/api/openapi/v1.json` contains the generated route and schema reference.

Responses from private workspaces use `Cache-Control: no-store`. The browser
retains only current-page thumbnails and selected images. Lock-only polling
does not decode or transfer images.

Errors use Problem Details: 400 for invalid input/session headers, 404 for
unknown or expired session resources, 409 for lock/version conflicts, and 422
for invalid resource data. The current lease coordinator supports one backend
process per source library. See [split architecture](split-architecture.md).
