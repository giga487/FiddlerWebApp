using Fiddler.Graphics;
using Fiddler.Graphics.Gumps;
using Fiddler.Formats.Catalog;
using UOAS.Application.Collaboration;
using UOAS.Contracts;

namespace UOAS.Backend.Endpoints;

public static class CollaborationEndpoints
{
    public static AssetWorkspace Workspace(HttpContext http, Guid id) => CollaborationGuard.Session(http).Workspace(id);
    public static void MapCollaboration(this WebApplication app)
    {
        app.MapPost("/api/sessions", (StartSessionRequest request, CollaborationSessions sessions) =>
        {
            try { return Results.Ok(sessions.Create(request.Name)); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
            catch (InvalidOperationException ex) { return Results.Problem(statusCode: 429, detail: ex.Message); }
        });
        var api = app.MapGroup("/api").AddEndpointFilter<CollaborationGuard>();
        api.MapGet("/session", (HttpContext http) => Results.Ok(CollaborationGuard.Session(http).Info()));
        api.MapDelete("/session", (HttpContext http, CollaborationSessions sessions) => { sessions.Close(CollaborationGuard.Session(http)); return Results.NoContent(); });
        api.MapGet("/libraries", (UOAS.Application.Storage.WorkspaceSettingsStore settings) => settings.Read().Libraries.Select(library => new AssetLibrary(library.Id, library.Name)));
        api.MapGet("/workspaces", (HttpContext http) => CollaborationGuard.Session(http).Workspaces.Values.Select(workspace => workspace.Info()));
        api.MapPost("/workspaces", (OpenWorkspaceRequest request, HttpContext http, CollaborationSessions sessions, UOAS.Application.Storage.WorkspaceSettingsStore settings) =>
        {
            string directory = LibraryConfiguration.Directory(settings, request.Library);
            return Results.Ok(sessions.Open(CollaborationGuard.Session(http), directory).Info());
        });
        api.MapGet("/workspaces/{workspaceId:guid}", (Guid workspaceId, HttpContext http) => Workspace(http, workspaceId).Info());
        api.MapPost("/workspaces/{workspaceId:guid}/heartbeat", (Guid workspaceId, HttpContext http) => { var workspace = Workspace(http, workspaceId); workspace.Heartbeat(); return Results.Ok(workspace.Info()); });
        api.MapPost("/workspaces/{workspaceId:guid}/refresh", (Guid workspaceId, HttpContext http) => { var workspace = Workspace(http, workspaceId); workspace.Refresh(); return Results.Ok(workspace.Info()); });
        api.MapDelete("/workspaces/{workspaceId:guid}", (Guid workspaceId, bool? discard, HttpContext http) =>
        {
            var session = CollaborationGuard.Session(http); var workspace = session.Workspace(workspaceId);
            if (workspace.Info().DraftCount > 0 && discard != true) throw new InvalidOperationException("Publish or explicitly discard drafts before closing the workspace.");
            session.Workspaces.Remove(workspaceId); workspace.Dispose(); return Results.NoContent();
        });
        api.MapGet("/workspaces/{workspaceId:guid}/files", (Guid workspaceId, HttpContext http, ClientDirectoryScanner scanner) => scanner.Scan(Workspace(http, workspaceId).Directory, http.RequestAborted));
        api.MapGet("/workspaces/{workspaceId:guid}/locks", (Guid workspaceId, HttpContext http) => Workspace(http, workspaceId).Locks());
        api.MapPost("/workspaces/{workspaceId:guid}/locks/{kind}/{id:int}", (Guid workspaceId, ResourceKind kind, int id, HttpContext http) => Results.Ok(Workspace(http, workspaceId).Acquire(kind, id)));
        api.MapDelete("/workspaces/{workspaceId:guid}/locks/{kind}/{id:int}", (Guid workspaceId, ResourceKind kind, int id, bool? discard, HttpContext http) => { Workspace(http, workspaceId).Release(kind, id, discard == true); return Results.NoContent(); });
        api.MapGet("/workspaces/{workspaceId:guid}/assets/{kind}", (Guid workspaceId, ResourceKind kind, int? offset, int? limit, bool? includeEmpty, int? id, HttpContext http) =>
            Workspace(http, workspaceId).Page(kind, offset ?? 0, limit ?? 24, includeEmpty ?? true, id));
        api.MapGet("/workspaces/{workspaceId:guid}/assets/{kind}/{id:int}", (Guid workspaceId, ResourceKind kind, int id, HttpContext http) => Workspace(http, workspaceId).Details(kind, id));
        api.MapGet("/workspaces/{workspaceId:guid}/assets/{kind}/{id:int}/image", (Guid workspaceId, ResourceKind kind, int id, bool? thumbnail, HttpContext http, SkiaAssetRenderer renderer) =>
        {
            var image = Workspace(http, workspaceId).Read(kind, id);
            return Results.File(thumbnail == true ? renderer.RenderThumbnail(image) : renderer.RenderPng(image), "image/png");
        });
        api.MapPut("/workspaces/{workspaceId:guid}/assets/{kind}/{id:int}/image", async (Guid workspaceId, ResourceKind kind, int id, HttpContext http) =>
        {
            if (http.Request.ContentType?.Split(';')[0] != "image/png") throw new ArgumentException("Upload image/png content.");
            await using var memory = new MemoryStream();
            var buffer = new byte[65536]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                if (memory.Length + count > GumpImageImporter.MaxUploadBytes) throw new ArgumentException("PNG upload exceeds 8 MiB.");
                await memory.WriteAsync(buffer.AsMemory(0, count), http.RequestAborted);
            }
            var workspace = Workspace(http, workspaceId); workspace.Put(kind, id, memory.ToArray()); return Results.Ok(workspace.Details(kind, id));
        });
        api.MapDelete("/workspaces/{workspaceId:guid}/assets/{kind}/{id:int}", (Guid workspaceId, ResourceKind kind, int id, HttpContext http) => { Workspace(http, workspaceId).Remove(kind, id); return Results.NoContent(); });
        api.MapPost("/workspaces/{workspaceId:guid}/publish/{kind}", async (Guid workspaceId, ResourceKind kind, HttpContext http, UOAS.Application.Storage.WorkspaceSettingsStore settings) =>
        {
            string output = settings.ExportDirectory();
            return Results.Ok(await Workspace(http, workspaceId).Publish(kind, output, http.RequestAborted));
        });
        ToolEndpoints.Map(api);
        PluginEndpoints.Map(api);
        ComparisonEndpoints.Map(api);
        DocumentEndpoints.Map(api);
        RegionEndpoints.Map(api);
        StorageEndpoints.Map(api);
        AtlasEndpoints.Map(api);
    }
}
