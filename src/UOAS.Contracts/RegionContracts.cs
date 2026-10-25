using System.Text.Json;

namespace UOAS.Contracts;

public sealed record RegionSource(string Id, string Name);
public sealed record OpenRegionsRequest(string Source);
public sealed record RegionVolume(int X1, int Y1, int Z1, int X2, int Y2, int Z2);
public sealed record RegionSummary(int Id, string Name, string Map, string Type, int Priority, string? ParentName, bool Draft, bool Deleted, DocumentOwner? Lock);
public sealed record RegionRecord(RegionSummary Summary, JsonElement Value, IReadOnlyList<RegionVolume> Area);
public sealed record RegionPage(int Total, IReadOnlyList<RegionSummary> Items);
public sealed record RegionCatalog(Guid Id, string Name, long Revision, int Count, int DraftCount, IReadOnlyList<string> Maps, IReadOnlyList<string> Types);
public sealed record RegionExport(string FileName, long Revision, string Json);
