# Color studio

The working unit is a hue and a user-configured board of objects. A palette edit
updates every board card, so the user can judge clothing, furniture, weapons and
Gumps together. Each card keeps an original image beside its recolored image.
The same asset can appear more than once, with different color modes.

## Workflow

1. Configure the client directory, then open the hue library at `/colors`.
2. Find a hue by name, decimal game ID or `0x` hexadecimal game ID. Choose it,
   duplicate it as a new hue, or create a neutral palette.
3. Browse Item/Gump thumbnails and click to add samples, or enter asset IDs.
   The Gump / Art inspector also links to the studio with the asset preselected.
4. Use Automatic (TileData) when adding Items to read the PartialHue flag,
   including session metadata drafts. Missing TileData and Gumps use full color.
   Override Full color or Gray pixels only independently on each card; the latter
   preserves already colored details.
5. Edit any of 32 samples, or generate a complete palette from three anchors.
   The anchors occupy slots 0, 15 and 31; interpolation occurs in RGB555 space.
   The color picker reports the quantized game color after each edit.
6. Undo recent changes, restore an existing source hue, or export a new file.

The UI consistently uses game hue IDs: 1 selects record 0 in hues.mul; 0 means
no hue. There is no index-offset toggle. The miniature library ramps show all
32 samples; the board shows their actual response on the selected source pixels.
Full color indexes the ramp with the source red channel's five-bit value, as
the normal UO art hue path does. Gray-only requires equal RGB555 channels.
Transparent pixels and source alpha remain unchanged. This covers Item/Gump
previews; lighting, spectral flags, effects and animation composition are outside
this first implementation.

## Format and exports

`Fiddler.Formats/Hues/HueDocument` reads complete 708-byte blocks: a four-byte
header and eight 88-byte records. Each record contains 32 little-endian ushort
colors, two metadata ushorts and a 20-byte ASCII name. Source block headers,
metadata and unedited records remain byte-exact. Edited names must use printable
ASCII within the file's 20-byte limit. Existing metadata remains unchanged;
new records start with zero metadata and new blocks start with zero headers.

New hues append without changing existing IDs. Exports pad the final block with
zero records to preserve the eight-entry layout. These padded slots appear as
unnamed hues when the exported file is reopened. The maximum supported library
is 16,376 entries (complete blocks within the 14-bit normal game hue range).
Empty, truncated and oversized source files are rejected rather than silently
truncated. A missing hues.mul starts a new empty document explicitly identified
in the interface; this is not a merge with a game library.

Exports create a unique `hues-<guid>/hues.mul` under the selected absolute server
folder. A failed write removes its incomplete output. Export keeps the session's
drafts available. Reopening with drafts requires the studio's explicit discard
action; failed opening preserves the current document and board.

## Sessions and limits

`ColorStudioWorkspace` is scoped to the Blazor session. It owns a separate
HueDocument and up to 24 sample snapshots with at most eight million combined
source pixels. Source images are not recolored in place. The board persists
through internal navigation, but reloads or server restarts discard it. Reopening
a library clears its board. Gump edits made elsewhere do not automatically refresh
an already added snapshot; remove and add the sample again.

Sample thumbnails fit within 192×160 px, with nearest-neighbor rendering and
100%/200% display zoom. Large images are reduced for comparison. The visual asset
picker offers 12, 24, 48, 96 or 192 previews per page and 100%–400% zoom.
The library lists 32 hues per page. Each hue
retains up to 50 undo steps. Rendering remains in Fiddler.Graphics; palette lookup,
file parsing, drafts and exports have no browser or Windows Forms dependencies.

Tests use generated fixtures for RGB555 quantization, full/partial recoloring,
alpha, gradient anchors, malformed files, unchanged byte round trips, editing,
undo, new IDs, block padding, source preservation, and session isolation. Real
client data and browser appearance require separate verification.
