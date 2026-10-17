# Gump import and comparison

The `/gump/manage` page uses the current workspace document. Edits remain separate
from the original archive until a new version is saved.

## Workflow

1. Open Gumps from the configured folder or arrive from the viewer with an ID selected.
2. Enter a target ID in decimal or hexadecimal with the 0x prefix.
3. Upload a PNG from the browser's computer and inspect its converted preview.
4. Click Start editing ID to acquire its edit lease, then Add to draft or Replace in draft. Changes also appear in your viewer.
5. Optionally open a comparison archive using an absolute server path.
6. Select a comparison row for side-by-side previews; copy the reference to the same or another ID.
7. Publish a new complete MUL/IDX version, merging your drafts with the latest shared version. The workspace refreshes and releases your leases.

Drafts belong to the Blazor session and survive internal navigation. Save a version
before closing or fully reloading the page. Reopening the workspace or changing
the configured folder is blocked while drafts exist. Undo by ID and discard-all
operations are available.
Drafts remain private. Other users can read the same ID and reserve different IDs
in the same archive. The same account in another tab has a separate editing
session. Lease expiry, version conflicts and publication are described in
[users and collaboration](users-and-collaboration.md).

## Containers

- MUL: gumpart.mul + gumpidx.mul; an explicit index path supports renamed comparison indexes.
- UOP v4/v5: canonical build/gumpartlegacymul/00000000.tga hashes identify Gumps independently of the external file name.
- Compression: 0 (stored), 1 (zlib), 3 (zlib plus Mythic). The first two decompressed int32 values contain dimensions.
- If both formats exist in the current folder, UOP is preferred, matching UOFiddler.
- IDs above the old 65535 limit are recognized up to 0x11FFF.

Hashing and Mythic decoding are adapted from UOFiddler's Ultima/Helpers/UopUtils.cs
and Ultima/Helpers/MythicDecompress.cs. They live in Formats without desktop dependencies.

## PNG conversion

PNG signature and dimensions are checked before decoding. Limits: 8 MiB upload,
4096 px per side and 4 million pixels. Gumps use 15-bit RGB and one-bit alpha:
alpha below 128 becomes transparent; other pixels become opaque. Quantized opaque
black maps to RGB 8,8,8 to avoid zero, which represents transparency. Previews show
the actual conversion result.

## Comparison

The comparer walks the union of IDs and decodes one resource at a time. Different
compression or RLE organization does not make images different when pixels and
dimensions match. RGB values of fully transparent pixels are ignored. Read errors
are reported per ID without stopping the full comparison. Results distinguish
Equal, Different, Current only, Reference only and Unreadable. Difference filters,
pagination, progress and cancellation are available. Importing, copying or undoing
changes invalidates previous results; run the comparison again for the new state.

## Saving

Publication reads the latest shared version and overlays only the current
session's changed IDs, preventing a stale session from dropping another user's
published edits. Publications for a source archive are serialized; ID leases
and per-ID versions are validated before writing. Shared version manifests
persist outside wwwroot under App_Data and survive restarts.

Export writes the merged complete document to a new subfolder of the selected server
destination. Unmodified raw records are preserved and drafts are included.
The index has at least 65536 rows and expands for higher IDs. Both files are built
in a private staging folder and published together by moving the completed folder.
Cancellation or failure does not publish a partial pair. Original overwriting
and UOP output are not supported.

Limits: 512 edits, 16 million pixels and 64 MiB of edited records per session;
16 MiB per MUL record, 32 MiB per UOP payload and 200000 UOP entries. Drafts and
comparison state are isolated per session.

## Validation

Tests cover PNG conversion, quantization, alpha, add/replace, undo, all five
comparison categories, UOP v4/v5 and compression modes, renamed files, high IDs,
malformed input, cancellation and reopening exports. Browser checks use synthetic
archives and cover copying to another ID, draft navigation, accidental reopen
protection, unchanged originals, saving and previewing the saved version.
Real client data still needs verification.
