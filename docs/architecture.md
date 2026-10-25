# Fiddler engine modules

The system separates binary containers, game resources and rendering. The
standalone UOAS.Frontend runs the interface in WebAssembly; UOAS.Backend composes
the reusable server libraries. See [split architecture](split-architecture.md).

```text
src/
  Fiddler.Formats/
    Containers/Mul/    # IDX entries, offsets, lengths and bounded byte reads
    Containers/Uop/    # v4/v5 indexes, name hashes, zlib and Mythic decoding
    Art/               # land/item mapping, reads, encoding, session drafts/export
    Fonts/             # ASCII/Unicode glyphs, masks, metrics, text preview/export
    Localization/      # Cliloc UTF-8 records, flags, drafts and complete export
    Metadata/          # legacy/extended TileData, preserved fields and export
    Editing/           # bounded source reads and staged resource exports
    Gumps/             # MUL/UOP archives, RLE, encoding, drafts, comparison, export
    Animations/        # six MUL layouts, bodies, actions, directions, palette/RLE
    Multis/            # legacy/High Seas MUL and MultiCollection UOP components
    Maps/              # terrain blocks, static records and bounded region reads
    Imaging/           # ARGB32 pixels, limits and shared color/read primitives
    Hues/              # RGB555 palettes, hue drafts, lookup and complete MUL export
    Catalog/           # inventory of files recognized by Fiddler
    Assets.cs          # shared catalog asset metadata
  Fiddler.Graphics/
    Art/               # Skia drawing from land/item records
    Gumps/             # Gump drawing, PNG import, grid composition and rendering
    Animations/        # stable anchors, frame drawing, atlas and playback clock
    World/             # bounded isometric scenes, warnings and pixel hit testing
    Imaging/           # bitmaps, canvas drawing, thumbnails and PNG encoding
  UOAS.Contracts/      # transport DTOs and product constants
  UOAS.Presentation/   # UI-independent MVVM commands and ViewModels
  UOAS.Frontend/       # standalone Blazor WASM views and SkiaSharp canvases
  UOAS.Application/    # workspaces, per-ID leases and shared publishers
  UOAS.Backend/        # ASP.NET Core API and collaboration session host
```

## Dependencies

- Formats has no SkiaSharp, ASP.NET or Windows dependencies.
- Art and Gump decoders share only pixel models and binary reading primitives.
- IndexedMulFile knows neither resource IDs nor image dimensions or animation layouts.
- Resource readers own and dispose their containers.
- Graphics depends on Formats and SkiaSharp, without referencing the webapp.
- Shared drawing stays in Imaging; resource-specific entry points stay in their modules.
- The UI uses MulWorkspace, AnimationWorkspace and WorldAssetWorkspace; it does not decode binary formats.

The previous mixed MulArchive/MulImageDecoder classes were replaced by dedicated
readers and decoders. SkiaAssetRenderer remains available for callers that already
have a decoded image.

## Resource status

| Module | Status | Responsibilities |
| --- | --- | --- |
| Art | MUL; UOP in scenes | Land and static items; original catalog remains MUL |
| Fonts | ASCII/Unicode and drafts | Glyph PNG replacement, signed offsets, text preview and export |
| Localization | Plain/compressed Cliloc | UTF-8 strings and flags; plain Cliloc export |
| Metadata | 32/64-bit TileData | Land/Item properties, flags and automatic PartialHue selection |
| Gumps | MUL/UOP and drafts | Images, import, comparison, complete new MUL export |
| Animations | MUL | Frames, directions, palettes, pivots and timing |
| Multis | 12/16-byte MUL and UOP | Components, coordinates, flags and isometric preview |
| Maps | MUL/UOP; MUL statics | Terrain/static regions and isometric preview |
| Sounds | File inventory | Audio is not decoded or played yet |
| Hues | MUL and drafts | 32-sample palette editing, Item/Gump sample board and complete new export |
| Containers/Uop | v4/v5 | Index, hashing and compression shared by supported readers |

Add new resources under Formats and, for visual resources, under Graphics.
Reuse shared containers without putting resource-specific logic in them. Do not
add empty readers or claim unsupported formats. Tests cover resource APIs, Skia
rendering, transparency, offsets, truncation, malformed input and session isolation.
