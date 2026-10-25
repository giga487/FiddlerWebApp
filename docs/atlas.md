# Atlas and region overlays

The `/atlas` page displays top-down terrain overviews from configured client
libraries. The same AtlasCanvas and presentation ViewModel are used by `/regions`
for interactive region boundaries. Browsers receive compressed images and bounded
region metadata, never MUL/UOP archives.

## Source configuration

Place mapN.mul or mapNlegacymul.uop and radarcol.mul under the configured storage
root, then register their folder as an asset library in Configuration. Atlas
discovers map files in each registered library. UOP takes precedence when both
formats exist. Missing colors, malformed archives or unknown dimensions appear
as per-map diagnostics rather than fabricated previews.

Standard facets 0–5 use validated dimension detection and the names Felucca,
Trammel, Ilshenar, Malas, Tokuno and TerMur. Custom file IDs 0–255 are supported.
In each library's Atlas map names and dimensions section, add an override with
file ID, server map name, width and height. Both dimensions 0 select standard
detection; otherwise both must be multiples of 8 from 8 to 16,384 and match the
archive's block count. Custom names must match the region Map strings, such as
Sea. Multiple registrations of one folder can provide different logical names
for the same physical archive and reuse its image cache.

Map overrides persist with application settings, for example:

```json
{
  "id": "client",
  "name": "Client",
  "directory": "Clients/Classic",
  "maps": [{ "fileId": 25, "name": "Sea", "width": 7168, "height": 4096 }]
}
```

## Backend rendering and cache

Radar colors are decoded from RGB555. The backend creates a shared 1:8 overview
by averaging the 64 terrain cells in each source block. MUL blocks are read in
sequential batches of at most 4,096 blocks, rather than allocating a full-size
world image. The largest configured map's base raster is 2,048 × 2,048 pixels.
UOP reading keeps the existing bounded chunk decoder.

Viewport images are 256 × 256 PNG tiles at scales 1, 2, 4, 8, 16, 32 and 64.
Fine levels read only the requested bounded source area; coarse levels aggregate
the shared overview. Thumbnails have a maximum side of 512 pixels. PNG is
lossless; both spatial aggregation and PNG compression reduce transfer size.
The renderer depicts terrain radar colors. Statics, map diffs, verdata, slopes
and isometric art rendering are outside this view.

Derived images live in backend App_Data/atlas-cache. Cache keys include source
paths, source/palette lengths and modification times, dimensions and renderer
version. Changed files invalidate the revision; stale tile requests receive 409
and must refresh the catalog. Preserve timestamps only if the source content is
unchanged; externally replacing content with identical lengths and timestamps
cannot be detected by this fingerprint.

Cache misses are coalesced and two workers generate images. A queue limit prevents
unbounded pending generation. Cancelled clients stop waiting while shared cache
work can finish. Every 32 generated images, oldest cached PNGs are pruned toward
a 512 MiB disk budget. Existing sources are never overwritten.

## Browser behavior

Maps open fitted to the viewport. Dragging pans, the wheel/buttons zoom around
the pointer/center, and image requests are debounced and cancelled when the
viewport changes. Detail stops at one screen pixel per terrain cell. At most
64 image tiles are selected for a viewport; a coarser level is used when needed.
The client keeps at most 128 compressed tiles and disposes unused Skia bitmaps.
The first 24 available maps receive thumbnails automatically; other ready cards
remain selectable. Refresh atlas reloads metadata and revisions.

## Region editing on the atlas

Open a region catalog and choose the matching atlas map. Visible region volumes
are requested separately from the background, filtered by map, XY viewport and
optional half-open Z slice. Overlays are paginated with a client budget of 1,000
regions/4,000 volumes. A visible notice asks the user to zoom in if truncated.

Yellow marks selection, purple drafts, green available regions and red regions
locked by another designer. Overlapping hits show an explicit region chooser
with priority and owner. Selecting a boundary focuses that record in the list
and loads its properties; selecting a list record focuses its map geometry.

Acquire the selected region before drawing, moving or resizing volumes. Drawing
adds a rectangle with Z -128..128; moving and resizing preserve the original Z
range. Resize uses the selected volume's lower-right corner. All gestures update
the form only. Save region draft validates ownership on the backend, and Publish
regions performs the existing lease/version/hierarchy validation. Unknown lock
status disables editing. Source region data and background tiles remain separate.

Without client map files, region property editing and the boundary-only preview
remain available. No geographic background is synthesized from region bounds.

## API and checks

- GET `/api/atlas/maps` lists ready/unavailable maps and library warnings.
- GET `/api/atlas/{library}/{facet}/thumbnail?revision=...` returns PNG.
- GET `/api/atlas/{library}/{facet}/tiles/{scale}/{x}/{y}?revision=...` returns PNG.
- GET `/api/regions/{catalog}/overlay?map=...&x=...&y=...&width=...&height=...`
  returns bounded region metadata, with optional z, offset and limit.

All routes use the existing collaboration session header and storage root checks.
AtlasTests validates MUL/UOP parity, cell coordinates, color aggregation, cache
reuse/invalidation, custom dimensions, viewport/Z filters and per-ID ownership.
tools/atlas-browser-smoke.mjs uses explicitly synthetic AtlasSmoke fixtures,
temporarily registers their sources and restores the original configuration.
It checks map panoramas, zoom, overlapping selection, volume gestures, named locks,
geometry publication and mobile layout with two WASM sessions.
