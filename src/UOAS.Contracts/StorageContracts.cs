using System.Text.Json.Serialization;

namespace UOAS.Contracts;

public sealed record StorageEntry(string Name, string Path, bool Directory, bool Blocked, long? Bytes);
public sealed record StoragePage(string Path, string? Parent, int Total, IReadOnlyList<StorageEntry> Entries);
public sealed record ConfiguredLibrary(string Id, string Name, string Directory, IReadOnlyList<MapDefinition>? Maps = null);
public sealed record ConfiguredRegionSource(string Id, string Name, string File, IReadOnlyList<string> Maps, IReadOnlyList<string> Types);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSettings(long Revision, IReadOnlyList<ConfiguredLibrary> Libraries, IReadOnlyList<ConfiguredRegionSource> RegionSources, string ExportDirectory);
public sealed record StorageStatus(bool Configured);
