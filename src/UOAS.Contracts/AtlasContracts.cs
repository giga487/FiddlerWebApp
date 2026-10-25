namespace UOAS.Contracts;

public sealed record MapDefinition(int FileId, string Name, int Width, int Height);
public sealed record AtlasMap(string Library, int FileId, string Name, int Width, int Height, string Revision, bool Ready, string? Detail, int TileSize = 256);
public sealed record AtlasCatalog(IReadOnlyList<AtlasMap> Maps, IReadOnlyList<string> Warnings);
public sealed record RegionOverlay(RegionSummary Summary, IReadOnlyList<RegionVolume> Area);
public sealed record RegionOverlayPage(int Total, IReadOnlyList<RegionOverlay> Items);
public sealed record RegionVolumeEdit(int? Index, RegionVolume Volume);
