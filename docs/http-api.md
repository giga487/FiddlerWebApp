# HTTP API v1

The API is hosted by the same application as the Blazor UI. It reuses Formats,
Graphics, shared publication and edit leases. It does not automate UI components.
All JSON property names use camelCase; enum values are English strings.

Sign in through the web application first. For this phase the API accepts its
existing HTTP-only `Fiddler.User` login cookie. There is no bearer-token endpoint,
M2M client, Google token forwarding or MCP server. Those are separate next steps
after validating these operations. Real Google sign-in still requires server
credentials; explicit development profiles can be used for local API testing.
See [users and collaboration](users-and-collaboration.md).

While signed in, download **`/api/openapi/v1.json`** for the generated OpenAPI
contract. It describes routes, request schemas, cookie authentication, required
CSRF/If-Match headers and binary PNG uploads. There is no Swagger UI dependency.

## Workspace lifecycle and concurrency

Create a workspace with `POST /api/v1/workspaces` and a JSON body such as
`{"directory":"D:/Data/Client"}`. An empty object uses your configured server
folder. Paths always refer to the backend computer. Each workspace owns its
readers, private drafts and edit context across HTTP requests, independently of
browser circuits. Multiple requests to the same workspace are serialized.

The returned `id` belongs to both the authenticated account and that particular
login. Another account or login receives 404, even if it knows the ID. Separate
API workspaces and browser tabs do not share private drafts. They do share
published versions and compete for the same per-ID edit leases.

Workspaces expire after 15 minutes without requests. Idle cleanup runs once a
minute and also occurs on lookup/list/create. At expiry, close, login revocation
or process shutdown, scopes are disposed and leases released. Unsaved drafts are
memory-only and can be lost. Published versions and shared manifests persist.
There are at most 8 workspaces per account and 64 per process (429 at capacity).

Every successful workspace response contains an `ETag`, initially `"1"`.
Before a mutation, send its **exact quoted value** in `If-Match`. Successful
mutations advance the version and return the next ETag. Missing If-Match gives
428; a stale value gives 412 without applying the requested mutation. Read the
workspace again and review its state before retrying. The version covers this
workspace's operations, not other users' published revisions. Use `refresh` to
adopt a newly published base while preserving your own drafts.

All non-GET/HEAD calls require a valid `X-CSRF-TOKEN`, obtained from
`GET /api/v1/antiforgery` with the same login cookie. This includes comparison
and heartbeat. Only those two workspace POST operations omit If-Match because
they do not change draft/publication state. Workspace creation needs CSRF but
has no existing workspace ETag.

An edit lease lasts two minutes. Send `POST .../heartbeat` every 30 seconds while
actively editing; successful workspace requests also renew live leases. A lease
that already expired cannot be silently renewed. Reserve its ID again; a newer
published ID version can require discarding the stale draft and refreshing.
Closing a workspace with drafts requires `?discardDrafts=true`, or explicit
publication/discard first. Concurrent writers should use separate workspaces.

## Endpoints

All paths below start with `/api/v1`. In the asset tables, `{w}` means
`/workspaces/{workspaceId}`. IDs are decimal integers. Gump IDs are numeric,
not file names. Standard pagination uses `offset=0&limit=24`, with limit 1–100.

| Method | Path | Result |
| --- | --- | --- |
| GET | `/me` | Current user ID, display name, email and provider |
| GET | `/antiforgery` | CSRF token and header name |
| GET | `/workspaces` | Workspaces owned by this login |
| POST | `/workspaces` | Open `directory`; 201, Location, ETag and workspace state |
| GET | `{w}` | Directory, version, shared revision, draft count and idle expiry |
| DELETE | `{w}` | Close; optional `discardDrafts=true`; 204 |
| POST | `{w}/heartbeat` | Renew activity and live leases; no If-Match |
| GET | `{w}/files` | File inventory, alternatives, missing files and validation findings |
| POST | `{w}/refresh` | Adopt latest shared Gumps, preserving private drafts |
| GET | `{w}/leases` | Owners/expiry of reserved Gump IDs; no fencing tokens |
| POST | `{w}/leases/gumps/{id}` | Reserve the ID for this workspace |
| DELETE | `{w}/leases/gumps/{id}` | Release an ID without a private draft; 204 |

| Method | Path | Result |
| --- | --- | --- |
| GET | `{w}/gumps` | Paginated IDs, encoded sizes and private draft flags |
| GET | `{w}/gumps/{id}` | Dimensions, encoded size and draft status |
| GET | `{w}/gumps/{id}/image` | PNG; optional `scale=1` or `thumbnail=true` |
| PUT | `{w}/gumps/{id}/image` | Import raw `image/png` bytes into a reserved ID |
| POST | `{w}/gumps/{id}/copy` | Copy `referenceId` from server `referencePath` into reserved target ID |
| POST | `{w}/gumps/compare` | Compare with server `referencePath`; no If-Match |
| DELETE | `{w}/gumps/{id}/draft` | Undo one private draft and release its lease; 204 |
| DELETE | `{w}/drafts` | Discard all private drafts and leases; 204 |
| POST | `{w}/publish` | Merge drafts into a new complete shared MUL/IDX directory |
| GET | `{w}/art/land` or `{w}/art/items` | Paginated Art MUL/IDX IDs |
| GET | `{w}/art/{kind}/{id}/image` | Art PNG; kind is `land` or `items` |

Copy body: `{"referencePath":"D:/Reference/gumpart.mul","referenceId":44}`.
Comparison body: `{"referencePath":"D:/Reference/gumps.uop","includeEqual":false,"offset":0,"limit":24}`.
Both accept an optional `referenceIndex` for a separately named MUL index.
`referencePath` can also be a folder with the standard Gump filenames.
Comparison returns full category counts and only the requested page of rows;
it still scans/decodes both archives to compute those counts. Closing the HTTP
request cancels comparison. There is no background comparison job.

PNG imports require an existing edit lease, accept up to 8 MiB, 4096 pixels per
side and 4 million decoded pixels, and use the Gump palette/one-bit alpha rules.
Publication body is `{}` or `{"outputDirectory":"D:/Exports"}`. The default is
`FiddlerExports` under the workspace directory. It returns the newly created
directory and shared revision, clears published private drafts and releases
their leases. It never replaces the original source files. Only Gumps currently
support mutations. There is no automatic download or replacement of game files.

| Method | Path | Result |
| --- | --- | --- |
| GET | `{w}/multis` | Paginated Multi IDs; `source=auto|mul|uop`, `rowSize=12|16` for MUL |
| GET | `{w}/multis/{id}` | Paginated component IDs, signed X/Y/Z, flags and extra values |
| GET | `{w}/multis/{id}/image` | Isometric PNG; optional `maxZ` and `includeInvisible=true` |
| GET | `{w}/maps/{facet}` | Format, block count, statics and detected dimensions |
| GET | `{w}/maps/{facet}/region` | Terrain/statics JSON; required `x`, `y`; `size=8` (1–16) |
| GET | `{w}/maps/{facet}/region/image` | Isometric PNG for the same region parameters |
| GET | `{w}/animations` | Available physical MUL archives and warnings |
| GET | `{w}/animations/{file}/bodies` | Paginated physical bodies |
| GET | `{w}/animations/{file}/bodies/{body}/actions` | Available action IDs |
| GET | `{w}/animations/{file}/bodies/{body}/actions/{action}/directions/{direction}` | Frame count, common frame dimensions, atlas columns and PNG URL |
| GET | `{w}/animations/{file}/bodies/{body}/actions/{action}/directions/{direction}/image` | Stable-anchor atlas PNG |

Map facets are 0–5. Maps accept `source=auto|mul|uop` and `includeStatics=true`.
For custom maps, supply both `width` and `height` in tiles. Region size bounds
the amount decoded/returned. Multi images accept the same source/rowSize as
their metadata. Scene PNGs report `X-Fiddler-Warning-Count` when Art is missing.
Animation `file` is 1–6; directions are 0–7, with 5–7 mirrored. The atlas supports
browser-side playback without per-frame requests. Definitions, body conversion,
hues and animation UOP are not implemented, as in the current UI.

## Try the API from the signed-in browser

Open the browser developer console on the application origin. This example
creates a workspace, lists Gumps, reserves an ID and compares an archive. Replace
the server paths with your own. It does not modify or publish any Gump image.

```javascript
const csrf = await (await fetch('/api/v1/antiforgery')).json();
let etag;
async function call(method, path, body, versioned = false) {
  const headers = { 'X-CSRF-TOKEN': csrf.token };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (versioned) headers['If-Match'] = etag;
  const response = await fetch('/api/v1' + path, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) throw await response.json();
  etag = response.headers.get('ETag') ?? etag;
  return response.status === 204 ? undefined : response.json();
}
const workspace = await call('POST', '/workspaces', { directory: 'D:/Data/Client' });
const root = '/workspaces/' + workspace.id;
const page = await call('GET', root + '/gumps?limit=24');
console.table(page.items);
const id = page.items[0]?.id ?? 44;
await call('POST', root + '/leases/gumps/' + id, undefined, true);
const comparison = await call('POST', root + '/gumps/compare', {
  referencePath: 'D:/Reference/gumpart.mul', includeEqual: false
});
console.table(comparison.results.items);
await call('DELETE', root, undefined, true);
```

For an external HTTP client, preserve the signed-in cookie and the antiforgery
cookie from `/antiforgery` in its cookie jar. Set `X-CSRF-TOKEN` and If-Match as
above. Raw imports use `Content-Type: image/png` with file bytes, not multipart
or base64 JSON. Cookie sessions are an interim validation mechanism; do not
treat the cookie as an M2M credential or forward a Google access token here.

## Errors and current boundaries

API failures use JSON Problem Details (`application/problem+json`), including
authorization failures and invalid routes/content types. API calls do not
redirect to HTML sign-in or error pages. Operational errors include a trace ID
for server-log correlation. Responses use `Cache-Control: no-store`.

| Status | Meaning |
| --- | --- |
| 400 | Invalid parameters/JSON or missing/invalid CSRF token |
| 401 / 403 | Sign-in required, expired/revoked login, or forbidden filesystem access |
| 404 | Missing archive/asset/workspace, another login's workspace, or expired workspace |
| 409 | ID reserved elsewhere, missing/lost lease, stale published ID, or drafts blocking close/release |
| 412 / 428 | Stale / missing workspace If-Match |
| 413 / 415 / 422 | PNG too large / unsupported content type / invalid archive or image data |
| 429 | Workspace capacity reached |
| 500 | Server/file operation failed; use trace ID and server logs |

Run one application process for shared editing. Distributed locks, roles,
per-folder access policy, a durable action/audit log, persistent private drafts,
background jobs and MCP/OAuth token issuance are not part of this phase. These
endpoints have the same server filesystem privileges as the existing signed-in
web editor. Keep game data and published versions outside `wwwroot`.

Validation uses ASP.NET Core TestServer HTTP integration tests and synthetic
MUL/UOP/animation fixtures. Tests cover real application cookies and CSRF,
cross-user/login isolation, cross-request drafts, UI/API lock contention,
concurrent ETags, merged publication, unchanged source files, upload errors,
pagination, scene/atlas PNGs, logout, idle expiry and OpenAPI generation. Real
Google login and production-size client archives require deployment validation.
