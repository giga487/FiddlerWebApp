using Fiddler.Graphics;
using UOAS.Contracts;

namespace UOAS.Backend.Endpoints;

public static class ComparisonEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        const string root = "/workspaces/{workspaceId:guid}/comparison";
        api.MapPost(root, async (Guid workspaceId, CompareRequest request, HttpContext http, UOAS.Application.Storage.WorkspaceSettingsStore settings) =>
        {
            var workspace = CollaborationEndpoints.Workspace(http, workspaceId);
            await workspace.CompareGumps(LibraryConfiguration.Directory(settings, request.Library), http.RequestAborted);
            return Results.Ok(workspace.Comparison(0, 24, true));
        });
        api.MapGet(root, (Guid workspaceId, int? offset, int? limit, bool? differencesOnly, HttpContext http) =>
            CollaborationEndpoints.Workspace(http, workspaceId).Comparison(offset ?? 0, limit ?? 24, differencesOnly ?? true));
        api.MapGet(root + "/reference/{id:int}/image", (Guid workspaceId, int id, HttpContext http, SkiaAssetRenderer renderer) =>
            Results.File(renderer.RenderPng(CollaborationEndpoints.Workspace(http, workspaceId).ReadReference(id)), "image/png"));
        api.MapPost(root + "/copy", (Guid workspaceId, CopyGumpRequest request, HttpContext http) =>
        { var workspace = CollaborationEndpoints.Workspace(http, workspaceId); workspace.CopyReference(request.SourceId, request.TargetId); return Results.Ok(workspace.Details(ResourceKind.Gump, request.TargetId)); });
    }
}
