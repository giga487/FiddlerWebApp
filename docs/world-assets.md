# Multis and maps

## Modules

- Formats/Multis/MultiArchive lists IDs and reads one Multi at a time. MUL rows use the explicit legacy 12-byte or High Seas 16-byte layout. UOP entries use build/multicollection/{id:D6}.bin with validated internal IDs, variable-length components and skipped cliloc references. housing.bin is a catalog, not a structure.
- Formats/Maps/MapArchive reads 8×8 blocks with a 4-byte header and 64 three-byte land tiles. Block addressing is blockX * blockHeight + blockY; tiles inside each block are row-major. Seven-byte static records contain ID, local coordinates, signed Z and hue.
- Formats/Art/ArtArchive provides on-demand MUL/UOP land and item image lookup.
- Graphics/World/IsometricSceneRenderer projects (X-Y)*22 and (X+Y)*22-Z*4, keeps original bitmap dimensions and provides simplified depth ordering, bounds and pixel-aware picking. No Blazor dependency.
- WorldAssetWorkspace owns readers per session. A successful open replaces/disposes previous readers; a failed open preserves them. It is independent of Gump drafts.

Binary layouts, Multi UOP visibility bits and standard facet sizes were checked
against the local UOFiddler source: Ultima/Multis.cs, MultiComponentList.cs,
TileMatrix.cs, Maps/MapSizes.cs, Uop/MapUopReader.cs and
Uop/LegacyMulFileConverter.cs. There is no desktop library or System.Drawing dependency.

## Access and memory

Original files are opened read-only on the backend. The browser receives PNGs
and metadata, never the full archive. MUL/UOP indexes stay in memory; assets are
read on demand. Map UOP chunks are matched by canonical facet hashes independently
of the external file name. A chunk contains at most 4096 blocks (802816 decompressed
bytes); the last chunk is cached. Physical entry order may differ from logical
chunk order; logical chunks must be consecutive and intermediate chunks complete.

Standard map dimensions are detected from facet ID and block count, allowing
one trailing block. Custom maps require explicit width/height matching the block
count; proportions are never guessed. Statics indexes must cover the map. An empty
index means no statics. Incomplete pairs or malformed records produce errors.
Disable statics at opening to load terrain without an incomplete static pair.
Optional diff patches are not applied.

Limits: 50000 components per Multi, 10000 pieces per preview, 16×16-tile regions,
16 million unique decoded Art pixels per scene, 2048 px per output side and
4 million output pixels. Tables have 64 rows per page. Missing Art produces
placeholders and up to 32 warnings per preview. Decoded images needed for picking
are released when the scene changes or the component is removed.

## Fidelity and validation

This is an isometric asset preview. It does not apply hues, tiledata, deformed
slopes, foliage, advanced client depth sorting, animations, verdata or diff patches.
Hue values remain visible in the table. Large structures may require a maximum-Z
filter or exceed preview limits. Multis, maps and Art are not written.

Synthetic tests cover legacy/HS MUL, stored/zlib Multi UOP, negative coordinates,
flags, cliloc lengths, map addressing on both axes, physically reordered UOP
chunks, chunk boundaries, static records, malformed input, Art UOP mapping,
projection and selection. Browser checks cover loading, navigation, errors,
inspection and mobile layout. Verify real client data before claiming game-renderer parity.
