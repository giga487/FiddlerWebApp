# Client resource editing

This milestone adds font, Cliloc and TileData editors, plus Item/Land image
replacement alongside the existing Gump editor. Texture, light and animation
frame editing, map/Multi structural editors and general UOP writing remain
separate work. This does not claim full parity with desktop UOFiddler.

## Common workflow

Configure the source folder under Configuration. Open a resource in its page,
edit session drafts, enter an absolute server output parent folder and export.
All exports use a new uniquely named folder and leave loaded client files
untouched. The complete resource is exported, including unmodified records.
Drafts remain after export; explicitly discard them before reopening a source.
Undo restores the loaded source entry, rather than maintaining a history stack.
Internal navigation preserves drafts; a new session or server restart does not.

These editors use private session drafts and independent file exports. They do
not acquire Gump collaboration leases or publish to other users. Existing Gump
editing and shared publishing remain available at `/gump/manage`.

## Fonts — `/fonts`

`fonts.mul` contains up to twenty ASCII font slots. Active fonts contain 224
glyphs, character codes 32–255. The editor preserves font headers and each
glyph's unknown byte. `unifont*.mul` has a 65,536-entry offset table and monochrome
glyphs with signed X/Y offsets. Offsets and bitmap rows are validated on load;
invalid/truncated resources are rejected.

Select a font index and glyph from the paged catalog, or enter a decimal/hex
character code to add a missing Unicode glyph. Download PNG, replace it from
PNG, clear it, or undo its draft. Unicode offsets can be changed independently
of the image. The text preview lays out the actual glyph images and offsets;
it does not substitute browser fonts.

PNG uploads are limited to 8 MiB; glyph dimensions to 255 × 255. Unicode uses
one-bit ink: alpha at least 128 sets a black pixel, with transparency as the
background. A solid white PNG background becomes ink, so use transparent PNGs.
ASCII quantizes visible colors to RGB555; transparent zero is reserved, so an
opaque pure-black import uses the lowest nonzero color. Font drafts are limited
to 1,024 changed glyphs and 64 MiB of encoded bitmap data. Text previews support
512 UTF-16 code units and the shared 4096-pixel/4-million-pixel image limits.
Export writes the complete original font family/file layout.

## Cliloc — `/cliloc`

Choose a `cliloc.*` file, search by numeric ID or text and select a string.
Edit its text and flag, add a new ID, remove an existing entry, or undo an ID.
Preserve client placeholders such as `~1_NAME~` when editing.

The parser supports plain Cliloc and Mythic-compressed sources. Export writes
plain Cliloc, preserving the six-byte header, entry flags, original record order
and untouched texts. Text is strict UTF-8; record lengths are unsigned 16-bit
byte counts, including strings longer than 32 KiB. Text must fit 65,535 UTF-8
bytes. Duplicate IDs, partial records and invalid UTF-8 are rejected rather than
silently exporting a salvaged file. Input is bounded to 64 MiB and one million
entries; additions/replacements are limited to 10,000 draft IDs and 64 MiB of
UTF-8 text. CSV import/export is not part of this milestone.

## TileData — `/tiledata`

Both Legacy32 and Extended64 layouts are supported. Detection checks the fixed
16,384 Land entries and complete 32-entry Item groups. If a length is ambiguous,
choose the format explicitly; files are never guessed from the client executable.
Input is limited to 8 MiB and at most 65,536 Item entries.

Search by name or decimal/hex ID. Edit the 20-byte ASCII name, raw hexadecimal
flags and selected named flag checkboxes. Land entries expose texture IDs. Item
entries expose weight, quality/layer, quantity, animation, hue byte, height,
miscellaneous/unknown fields, stacking offset and value. The editor retains
headers, untouched records and flags not represented by checkboxes. Legacy
exports reject flags above 32 bits; Extended64 preserves all 64 bits.

The catalog inspector links graphics to their TileData entry and back. Color
studio's **Automatic (TileData)** option uses `PartialHue` (`0x40000`) for Items,
including this session's saved metadata drafts. Missing TileData and Gumps fall
back to full color. Manual per-sample overrides remain available. Existing board
samples are snapshots: re-add them to apply a changed automatic mode.

## Item and Land graphics — `/art/manage`

Open `art.mul` + `artidx.mul`, select the collection and ID, and import a PNG to
add or replace that slot. Imports are encoded immediately, then decoded for the
preview, so it displays the stored RGB555 colors and transparency. Download the
converted PNG, remove a slot, undo it, or discard all Art drafts.

Items use row-offset RLE and one-bit alpha (at least 128 is visible). Land images
must be exactly 44 × 44 pixels: only the 1,012-pixel opaque diamond is stored;
corner pixels are discarded. Item images must also fit the format's 16-bit
row-offset table. Shared PNG limits are 8 MiB, 4096 px per side and four million
pixels. Art drafts are limited to 512 slots and 64 MiB of encoded records.

The complete MUL/IDX pair is written together in a new directory. Unmodified raw
records and their IDX extra values are preserved; unchanged graphics are not
re-encoded. Added, replaced and removed IDs are visible in the current session's
Art catalog, Color studio asset picker and plugin asset reads. Already-added
sample board images remain snapshots. Map/Multi readers maintain their own
source archives and do not consume these drafts. Art UOP editing/export is not
implemented; the existing UOP scene readers remain read-only.

## Verification

Synthetic fixtures cover both TileData layouts and high flag bits, group headers
and unknown fields, long UTF-8 Cliloc strings, Unicode packed rows and signed
offsets, ASCII headers, malformed sources, PNG/rendered pages, randomized Item
RLE round trips, Land diamond shape, added/removed slots, source preservation,
and private Art drafts across sessions. Real client variants should also be
checked before using exports in a distributed game client.
