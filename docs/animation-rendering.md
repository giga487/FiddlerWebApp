# Gump and animation rendering

Formats, Graphics and the webapp remain separate projects. Gumps support MUL/IDX
and UOP; animations currently support the six MUL archives.

## Behavior checked against the fork

UOFiddler source files examined:

- Ultima/Animations.cs: GetFileIndex, GetActionCapacity, explicit animX/AnimationFrame modes.
- UoFiddler.Controls/UserControls/AnimationListControl.cs: bodies, actions, directions and action names.
- UoFiddler.Controls/UserControls/AnimatedPictureBox.cs: initial 150 ms delay and positioning.
- UoFiddler.Controls/Classes/AnimatedFrameListExtensions.cs: common surface and anchoring.

MUL sequences contain a 256-color palette, frame offset table, signed X/Y centers
and palette-index runs. Offsets are relative to the frame count after the 512-byte
palette. Runs have signed 10-bit coordinates, 12-bit lengths and a 0x7FFF7FFF terminator.
Five stored directions become eight: 5, 6 and 7 reuse 3, 2 and 1, mirroring pixels
and the horizontal center. IDX layouts cover all six archives, including body 34
in anim5. Action capacity checks prevent reading into the next body's block.

## Pivots and SkiaSharp drawing

Each frame rectangle relative to its anchor has left = -CenterX and
top = -CenterY - Height. The union determines the common drawing surface.
Frames retain the same anchor even when dimensions change. Mirroring uses
CenterX = Width - CenterX, matching Fiddler.

AnimationRenderer.DrawFrame draws onto any SKCanvas. AnimationPlayer manages
the current frame, play/pause, seeking and looping. The host calls Advance with
elapsed time and Draw with a canvas: there are no Windows timer, Blazor or HTTP
dependencies. The initial delay is 150 ms and can be changed.

## Web playback

SkiaSharp generates a PNG atlas of aligned cells on the server. The component
sends it once per sequence. Browser requestAnimationFrame replays the cells;
SkiaSharp does not run in WebAssembly and images are not transferred per tick.
Playback controls operate locally. The clock pauses in hidden tabs; animation
callbacks and listeners are removed when changing page or sequence. Zoom uses
nearest-neighbor sampling.

## Limits and validation

Archive selection exposes physical IDs, like Fiddler's explicit animX mode.
body.def, bodyconv.def, mobtypes.txt, hues, verdata, animation UOP and equipment
composition are not applied. See [Gump management](gump-management.md) for Gump import/comparison.

Synthetic tests cover palettes, alpha, signed coordinates, positive/negative
pivots, mirroring, all six layouts, sparse indexes, truncated records, atlas
generation and playback timing. Browser validation covers no per-frame HTTP
requests, pause, loop, seeking, archive changes, delay, mirrored direction,
Gump navigation and mobile layout. Real client data and the target Linux server
still need runtime verification.
