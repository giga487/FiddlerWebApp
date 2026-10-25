# Hue studio

Open `/colors`, find available documents and open hues.mul. Search by name or
select a decimal record ID. Acquire the hue's ID lock before changing its name
or any of its 32 RGB555 samples. Other designers can inspect it while it is locked.

Generate a gradient with shadow, middle and highlight colors; copy another hue's
colors; undo local palette edits; save the record draft and publish a new hues.mul
version. To append a hue, find Count + 1, acquire that ID, then generate or copy
its palette. Undo and release discards that record's saved draft and lease.

The sample board accepts up to eight Item, Land or Gump IDs from the current
library. Each card places the original beside its recolored image. Choose full,
gray-only or automatic mode and a checkerboard, light or dark background. The
automatic Item mode reads TileData PartialHue, including metadata drafts in an
open document. Missing TileData and non-Item graphics use full coloring.

PNG samples download once when added. SkiaSharp applies subsequent palette
changes locally; the browser never downloads MUL/IDX archives. Coloring matches
the native UO lookup: red-channel RGB555 intensity selects one of 32 colors;
gray-only mode preserves pixels whose quantized red, green and blue differ.
Transparency is retained. Changing samples and palettes does not edit their art.

The board's coloring mode and background apply to every card. It is a temporary
inspection board; published palette records are persisted on the backend.
