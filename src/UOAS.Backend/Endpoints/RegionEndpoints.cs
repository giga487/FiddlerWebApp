using System.Text.Json;
using UOAS.Application.Regions;
using UOAS.Contracts;

namespace UOAS.Backend.Endpoints;

public static class RegionEndpoints
{
    private static RegionWorkspace Workspace(HttpContext http, Guid id) => CollaborationGuard.Session(http).RegionWorkspaces.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Region catalog not found in this session.");
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/region-sources", (UOAS.Application.Storage.WorkspaceSettingsStore settings) => settings.Read().RegionSources.Select(source => new RegionSource(source.Id, source.Name)));
        api.MapPost("/regions", (OpenRegionsRequest request, HttpContext http, UOAS.Application.Storage.WorkspaceSettingsStore settings, RegionStore store) =>
        {
            if (string.IsNullOrEmpty(request.Source) || request.Source.Length > 80 || request.Source.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("Select a configured region source.");
            var source = settings.Region(request.Source);
            string file = settings.RegionFile(request.Source);
            var session = CollaborationGuard.Session(http);
            if (session.RegionWorkspaces.Count >= 4) throw new InvalidOperationException("Close a region catalog before opening another.");
            var workspace = store.Open(file, source.Name, session.Id, session.Name, source.Maps.ToArray(), source.Types.ToArray());
            session.RegionWorkspaces.Add(workspace.Id, workspace); return workspace.Info();
        });
        const string root = "/regions/{catalogId:guid}";
        api.MapGet(root, (Guid catalogId, HttpContext http) => Workspace(http, catalogId).Info());
        api.MapGet(root + "/records", (Guid catalogId, int? offset, int? limit, string? map, string? search, HttpContext http) => Workspace(http, catalogId).Page(offset ?? 0, limit ?? 24, map, search));
        api.MapGet(root + "/records/{id:int}", (Guid catalogId, int id, HttpContext http) => Workspace(http, catalogId).Read(id));
        api.MapPost(root + "/records", (Guid catalogId, HttpContext http) => Workspace(http, catalogId).Create());
        api.MapPut(root + "/records/{id:int}", (Guid catalogId, int id, JsonElement value, HttpContext http) => { var workspace = Workspace(http, catalogId); workspace.Put(id, value); return workspace.Read(id); });
        api.MapDelete(root + "/records/{id:int}", (Guid catalogId, int id, HttpContext http) => { Workspace(http, catalogId).Delete(id); return Results.NoContent(); });
        api.MapGet(root + "/locks", (Guid catalogId, HttpContext http) => Workspace(http, catalogId).Locks());
        api.MapPost(root + "/locks/{id:int}", (Guid catalogId, int id, HttpContext http) => Workspace(http, catalogId).Acquire(id));
        api.MapDelete(root + "/locks/{id:int}", (Guid catalogId, int id, bool? discard, HttpContext http) => { Workspace(http, catalogId).Release(id, discard == true); return Results.NoContent(); });
        api.MapPost(root + "/refresh", (Guid catalogId, HttpContext http) => { var workspace = Workspace(http, catalogId); workspace.Refresh(); return workspace.Info(); });
        api.MapPost(root + "/heartbeat", (Guid catalogId, HttpContext http) => { var workspace = Workspace(http, catalogId); workspace.Heartbeat(); return workspace.Info(); });
        api.MapPost(root + "/publish", (Guid catalogId, HttpContext http, UOAS.Application.Storage.WorkspaceSettingsStore settings) => Workspace(http, catalogId).Publish(settings.ExportDirectory()));
        api.MapGet(root + "/export", (Guid catalogId, HttpContext http) => Workspace(http, catalogId).Export());
        api.MapDelete(root, (Guid catalogId, bool? discard, HttpContext http) =>
        {
            var workspace = Workspace(http, catalogId);
            if (workspace.DraftCount > 0 && discard != true) throw new InvalidOperationException("Publish or undo region drafts before closing the catalog.");
            CollaborationGuard.Session(http).RegionWorkspaces.Remove(catalogId); workspace.Dispose(); return Results.NoContent();
        });
    }
}
