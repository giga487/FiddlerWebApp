namespace UOAS.Contracts;

public sealed record StartSessionRequest(string Name);
public sealed record CollaborationSession(string Token, string Name, DateTimeOffset ExpiresAt);
public sealed record OpenWorkspaceRequest(string Library);
public sealed record AssetLibrary(string Id, string Name);
public sealed record WorkspaceState(Guid Id, string Directory, long Version, long SharedRevision, int DraftCount, int ArtDraftCount = 0, int GumpDraftCount = 0);
public enum ResourceKind { Land, Item, Gump }
public sealed record AssetSlot(ResourceKind Kind, int Id, bool Empty, bool Draft, int EncodedBytes, ResourceLock? Lock);
public sealed record AssetPage(int Offset, int Limit, int Total, IReadOnlyList<AssetSlot> Items);
public sealed record ResourceLock(ResourceKind Kind, int Id, string UserName, bool Own, DateTimeOffset ExpiresAt, bool Saving);
public sealed record AssetDetails(ResourceKind Kind, int Id, bool Empty, bool Draft, int Width, int Height, ResourceLock? Lock);
public sealed record ExportResult(string Directory, long Revision);
public sealed record ApiProblem(string? Title, string? Detail, int? Status);
public enum DocumentKind { Hue, Cliloc, TileDataLand, TileDataItem, Font }
public sealed record DocumentFile(DocumentKind Kind, string FileName);
public sealed record OpenDocumentRequest(DocumentKind Kind, string FileName);
public sealed record DocumentInfo(Guid Id, DocumentKind Kind, string FileName, int Count, int DraftCount, long Revision);
public sealed record DocumentOwner(string UserName, bool Own, DateTimeOffset ExpiresAt);
public sealed record DocumentLock(int Id, string UserName, bool Own, DateTimeOffset ExpiresAt);
public sealed record DocumentRecord(int Id, System.Text.Json.JsonElement Value, bool Draft, DocumentOwner? Lock);
public sealed record DocumentPage(int Total, IReadOnlyList<DocumentRecord> Items);
public sealed record CompareRequest(string Library);
public sealed record ComparisonRow(int Id, string Difference, string? Detail);
public sealed record ComparisonPage(int Total, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<ComparisonRow> Items);
public sealed record CopyGumpRequest(int SourceId, int TargetId);
public sealed record AnimationArchive(int File, string Name, int Bodies);
public sealed record AnimationBodyInfo(int Id, string Category);
public sealed record AnimationActionInfo(int Id, string Name, IReadOnlyList<int> Directions);
public sealed record PackageCommand(string Id, string Name, string Description);
public sealed record PluginPackage(string Key, string FileName, string EntryAssembly, string Sha256, bool Trusted, bool Active, string? Name, string? Version, string? Description, IReadOnlyList<PackageCommand> Commands);
public sealed record InstallPluginRequest(string FileName, string EntryAssembly, string ContentBase64);
public sealed record ExecutePluginRequest(Guid WorkspaceId, ResourceKind Kind, int Id, string Command);
public sealed record PluginOutput(string Text);
