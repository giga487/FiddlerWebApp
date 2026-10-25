using UOAS.Application.Storage;
using UOAS.Contracts;

namespace UOAS.Backend.Endpoints;

public static class StorageEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/storage", (StorageRoot storage) => new StorageStatus(storage.Configured));
        api.MapGet("/storage/entries", (string? path, int? offset, int? limit, StorageRoot storage) => storage.Browse(path ?? "", offset ?? 0, limit ?? 50));
        api.MapGet("/configuration", (WorkspaceSettingsStore settings) => settings.Read());
        api.MapPut("/configuration", (WorkspaceSettings request, WorkspaceSettingsStore settings, HttpContext http, ILogger<WorkspaceSettingsStore> logger) =>
        {
            var saved = settings.Save(request);
            logger.LogInformation("Workspace configuration revision {Revision} saved by {Designer}", saved.Revision, CollaborationGuard.Session(http).Name);
            return saved;
        });
    }
}
