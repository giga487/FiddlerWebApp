# Fiddler Web App

Ultima Online asset manager: Blazor Web App (.NET 10), Fluent UI Blazor 5 and SkiaSharp rendering.
One application to run, with reusable readers and graphics in separate projects.
All project text uses English, including UI labels, messages, accessibility text,
catalog categories and documentation. The web host uses the en-US culture.

## Architecture

```text
FiddlerWebApp --> Fiddler.Graphics --> Fiddler.Formats
  Blazor/UI       SkiaSharp           IDX/MUL/UOP and ARGB32 pixels
```

- `Fiddler.Formats`: containers, decoders, Gump drafts, comparison and export. No SkiaSharp or UI dependencies.
- `Fiddler.Graphics`: bitmaps, SKCanvas drawing, PNGs, thumbnails and compositions. No Blazor, HTTP, Windows Forms or System.Drawing dependencies.
- `FiddlerWebApp`: separate user sessions, collection selection, decimal/hex ID search, pagination, previews and zoom.
- `Fiddler.Tests`: synthetic fixtures covering decoders, indexes, session isolation and Skia rendering.

See [module architecture](docs/architecture.md). Art, Gumps, Animations, Multis and
Maps have dedicated readers. Containers handle binary storage; Imaging holds
shared pixel and drawing primitives. `Graphics/World` renders isometric Multi and
map previews. Sounds currently have file inventory support only.

## Run

Requires .NET SDK 10. From this repository:

```powershell
dotnet run --project src/FiddlerWebApp -- --Mul:Directory="D:\Data\Client"
```

Open the localhost URL printed in the console and click **Open archive**.
Alternatively, open **Configuration**, enter an absolute server folder path,
click **Scan folder**, then **Save folder**. Saved settings override the initial
`Mul:Directory` value and are stored in `App_Data/client-directory.json`, outside
`wwwroot` and excluded from Git. The folder setting is application-wide; readers
already open in other sessions remain active until those sessions reopen them.
The path belongs to the computer running the backend, rather than the browser.
You can also set `Mul:Directory` in appsettings.json or use `Mul__Directory`.
Game files remain outside the repository and wwwroot.

## File scan and supported formats

`Fiddler.Formats/Catalog/UOFiddlerFiles.txt` snapshots the 131 file names in
`Ultima.Files._uoFiles` from the workspace UOFiddler fork. Update the snapshot
when that list changes. It is embedded in Formats; no desktop installation is needed.

The scanner checks only the selected folder. It reports present/missing files,
MUL/UOP alternatives, incomplete pairs or triples, empty files, index alignment,
readability and case collisions. Empty statics and optional definitions are
allowed. Unknown files are listed separately. File name matching ignores case.
Requirements depend on client version and features: missing optional files do
not necessarily indicate a broken client. The scan is an inventory and basic
index check, rather than complete archive integrity validation.

| Files | Collection |
| --- | --- |
| `artidx.mul` + `art.mul` | Land IDs 0–0x3FFF; item ID = index slot minus 0x4000 |
| `gumpidx.mul` + `gumpart.mul` | Gumps; dimensions packed in the index extra field |
| `gumpartLegacyMUL.uop` | Gump UOP v4/v5; stored, zlib and Mythic |
| `anim.idx` + `anim.mul`, and anim2–anim6 | Bodies, actions and eight directions |
| `multi.idx` + `multi.mul` / `MultiCollection.uop` | Multi components and previews |
| `mapN.mul` / `mapNLegacyMUL.uop` | Land regions for maps 0–5 |
| `staidxN.mul` + `staticsN.mul` | Optional map statics |
| `artLegacyMUL.uop` | Art images for Multi/map scenes |

Animation UOP, verdata and diff patches are not implemented. Hue editing and
application to a configurable Item/Gump sample board are available in Color studio;
the other catalogs and world/animation previews still show original colors.
Empty slots are hidden; corrupt asset records are reported individually.
Opening a MUL reads its index, then only the requested records (24 thumbnails
per catalog page). Limits: 12 MB index, 16 MiB record, 4096 px per image side and
4 million pixels per decoded image.

## Gumps and animations

Use `/gump` and `/animations`. Click **Open animations** and choose archive,
body, action and direction. Controls include category/ID filters, play/pause,
loop, frame stepping, seeking, delay and zoom. The default delay is 150 ms,
matching UOFiddler's AnimatedPictureBox.

Original pivots are preserved and directions 5–7 are mirrored. A common frame
surface prevents motion caused only by changes in frame dimensions. SkiaSharp
generates an atlas PNG on the server; browser requestAnimationFrame replays its
cells without per-frame network requests. Playback pauses in hidden tabs.
Navigation uses physical archive IDs, like explicit animX mode in UOFiddler.
body.def, bodyconv.def, mobtypes.txt, equipment composition and hues are not applied.
Animal and monster action names follow Fiddler; other bodies show action IDs.
Sequence limits: 512 frames, 16 million decoded pixels and 16 million atlas pixels.
See [animation rendering](docs/animation-rendering.md).

## Reuse the graphics engine

Reference `src/Fiddler.Graphics/Fiddler.Graphics.csproj` from another site or tool:

```csharp
using Fiddler.Formats.Art;
using Fiddler.Graphics;

using var archive = ArtMulReader.Open("artidx.mul", "art.mul");
var image = archive.ReadItem(0x1234);
var renderer = new SkiaAssetRenderer();
byte[] png = renderer.RenderPng(image);
// Or call renderer.Draw(canvas, image, destination) on your own SKCanvas.
// CreateBitmap transfers bitmap ownership and disposal to the caller.
```

For Gumps, use `Fiddler.Formats.Gumps.GumpMulReader.Open("gumpidx.mul", "gumpart.mul")`
and `Read(id)`. ArtRenderer and GumpRenderer also draw directly from raw records.
Decoded images use unpremultiplied ARGB32; rendering preserves transparency and
uses nearest-neighbor sampling. PNGs are generated on the server. SkiaSharp
includes native Windows/macOS dependencies; Linux uses
`SkiaSharp.NativeAssets.Linux.NoDependencies`. Runtime verification on the target
Linux server is still required.

## Import and compare Gumps

Open **Import and compare Gumps** or `/gump/manage`. Add or replace a Gump from
PNG, compare a second MUL/IDX or UOP, or copy a reference ID to the selected target.
Skia previews show the actual 15-bit RGB, one-bit alpha conversion. Changes are
visible throughout the workspace and can be undone. Saving writes a complete
MUL/IDX pair in a unique new subfolder and preserves original files.
PNG uploads come from the browser's PC; comparison and output paths are on the
server. Comparison uses decoded pixels and reports Equal, Different, Current
only, Reference only and Unreadable. See [Gump management](docs/gump-management.md).

## Create colors on a sample board

Open **Color studio** (`/colors`) and **Open configured library**. Choose an
existing hue or **Create a new hue**. Add Items and Gumps through **Browse assets
visually**, by decimal/hex ID, or through the link in the original catalog's
inspector. The board accepts up to 24 samples, including the same object in
different modes. Every card compares its original image with the current palette.

Edit any of the 32 RGB555 samples or generate a palette from shadow, midtone and
highlight colors. All cards update together. Choose **Full color** or **Gray
pixels only** per card, and use light/dark/checkerboard backgrounds and zoom.
Undo, restore and duplicate support experimentation. No tiledata flags are inferred.
Game hue IDs are one-based; hue 0 means the original image. New hues append after
the source entries without changing existing IDs.

**Export new hues.mul** writes a complete library to a new unique subfolder under
the absolute server path you enter. It preserves source headers and unedited
records, pads new blocks to eight entries, and never overwrites the loaded file.
Drafts remain after export. A folder without hues.mul starts an empty library;
its export contains only newly created hues. Board snapshots and palette drafts
survive internal navigation in the same session, but not reloads or server restarts.
See [Color studio](docs/color-studio.md) for format details and limits.

## Compose Gumps on a grid

Open **Compose Gumps on a grid** or `/gump/composition`. Add pieces by decimal
or hexadecimal ID. Each image is placed in the first free grid space at its
original size; multiple instances of an ID are allowed. Configure the board and
cell size, with 50%, 100% and 200% zoom. Select a piece on the board or in the list
to inspect its ID, coordinates and dimensions. Move it through X/Y or enable
**Move here with a click on the board**. **Arrange without overlaps** repacks the
pieces; manual placement permits overlaps. The last added piece is on top.

The model and Skia renderer live in `Fiddler.Graphics/Gumps`, without Blazor
dependencies. Compositions survive internal navigation within the session;
they are not saved to disk and do not change archives. Images are snapshots:
after editing an asset, use **Refresh image from workspace** for each affected
instance. Original game layouts are not inferred. Limits: 2048×2048 px board,
4–256 px cells, 128 pieces and 16 million pixels. Failed resizing preserves the
previous composition.

## Multis and map tiles

**Multi** (`/multi`) opens MUL/IDX or MultiCollection UOP. For MUL, explicitly
choose legacy 12-byte or High Seas 16-byte components; lengths can be ambiguous.
Find decimal/hex IDs, step through Multis, filter maximum Z or include flags 0.
The component list shows Art ID, X/Y/Z and flags.

**Maps** (`/maps`) reads maps 0–5 from MUL/UOP, with optional staidx/statics pairs.
Standard dimensions are detected per facet; custom maps require width/height
in tiles. Explore 4×4, 8×8 or 16×16 regions using coordinates or cardinal buttons.
Only relevant blocks are read: 196 bytes per 8×8 MUL block, or up to 4096 blocks
per UOP chunk. The last UOP chunk is cached. Stored/zlib chunks are resolved by hash.

Both views use Art MUL/IDX or ArtLegacyMUL UOP, preferring UOP automatically.
Missing images have magenta placeholders and warnings. Select a piece to inspect
its ID and coordinates; tables have 64 rows per page. Zoom is 50%, 100% or 200%.
These are asset previews: hues, tiledata, deformed slopes, diff patches, verdata
and animated statics are not applied. The original Land/Items catalog still
requires Art MUL/IDX; Art UOP is available in the new scenes. Multis and maps
are read-only. See [world assets](docs/world-assets.md).

English routes are used by all links and documentation. Previous routes remain
available as compatibility aliases for existing bookmarks.

## Validation

```powershell
dotnet build FiddlerWebApp.sln -c Release
dotnet test tests/Fiddler.Tests -c Release
```

Fixtures are generated in tests and contain no game assets. Original archives
are opened read-only and closed when the session ends. There is no shared mutable
graphics state across users. Real client data still needs verification.

## Next steps

Current work focuses on formats and visualization. Google login, authorization,
publishing and downloads will follow. This development app uses localhost;
integrate access control before exposing it to the Internet.
