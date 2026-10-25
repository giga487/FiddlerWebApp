using System.Text.Json;
using UOAS.Application.Collaboration;
using UOAS.Contracts;

namespace UOAS.Backend.Endpoints;

public static class DocumentEndpoints
{
    private static RecordWorkspace Document(HttpContext http, Guid workspaceId, Guid documentId) =>
        CollaborationEndpoints.Workspace(http, workspaceId).Documents.GetValueOrDefault(documentId) ?? throw new KeyNotFoundException("Document is not open in this workspace.");
    public static void Map(RouteGroupBuilder api)
    {
        const string root = "/workspaces/{workspaceId:guid}/documents";
        api.MapGet(root + "/files", (Guid workspaceId, HttpContext http) => SharedDocumentStore.Files(CollaborationEndpoints.Workspace(http, workspaceId).Directory));
        api.MapPost(root, (Guid workspaceId, OpenDocumentRequest request, HttpContext http) => CollaborationEndpoints.Workspace(http, workspaceId).OpenDocument(request.Kind, request.FileName));
        api.MapGet(root + "/{documentId:guid}", (Guid workspaceId, Guid documentId, HttpContext http) => Document(http, workspaceId, documentId).Info());
        api.MapGet(root + "/{documentId:guid}/locks", (Guid workspaceId, Guid documentId, HttpContext http) => Document(http, workspaceId, documentId).Locks());
        api.MapGet(root + "/{documentId:guid}/text", (Guid workspaceId, Guid documentId, int font, string text, HttpContext http, Fiddler.Graphics.SkiaAssetRenderer renderer) => Results.File(renderer.RenderPng(Document(http, workspaceId, documentId).RenderText(font, text)), "image/png"));
        api.MapDelete(root + "/{documentId:guid}", (Guid workspaceId, Guid documentId, bool? discard, HttpContext http) =>
        {
            var document = Document(http, workspaceId, documentId);
            if (document.DraftCount > 0 && discard != true) throw new InvalidOperationException("Publish or discard the document's drafts before closing it.");
            CollaborationEndpoints.Workspace(http, workspaceId).Documents.Remove(documentId); document.Dispose(); return Results.NoContent();
        });
        api.MapGet(root + "/{documentId:guid}/records", (Guid workspaceId, Guid documentId, int? offset, int? limit, int? id, string? search, HttpContext http) => Document(http, workspaceId, documentId).Page(offset ?? 0, limit ?? 24, id, search));
        api.MapGet(root + "/{documentId:guid}/records/{id:int}", (Guid workspaceId, Guid documentId, int id, HttpContext http) => Document(http, workspaceId, documentId).Read(id));
        api.MapPut(root + "/{documentId:guid}/records/{id:int}", (Guid workspaceId, Guid documentId, int id, JsonElement value, HttpContext http) =>
        { var document = Document(http, workspaceId, documentId); document.Put(id, value); return Results.Ok(document.Read(id)); });
        api.MapPost(root + "/{documentId:guid}/locks/{id:int}", (Guid workspaceId, Guid documentId, int id, HttpContext http) => Document(http, workspaceId, documentId).Acquire(id));
        api.MapDelete(root + "/{documentId:guid}/locks/{id:int}", (Guid workspaceId, Guid documentId, int id, bool? discard, HttpContext http) =>
        { Document(http, workspaceId, documentId).Release(id, discard == true); return Results.NoContent(); });
        api.MapPost(root + "/{documentId:guid}/refresh", (Guid workspaceId, Guid documentId, HttpContext http) =>
        { var document = Document(http, workspaceId, documentId); document.Refresh(); return Results.Ok(document.Info()); });
        api.MapPost(root + "/{documentId:guid}/publish", async (Guid workspaceId, Guid documentId, HttpContext http, IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            string output = Path.GetFullPath(configuration["Assets:ExportDirectory"] ?? "App_Data/exports", environment.ContentRootPath);
            return Results.Ok(await Document(http, workspaceId, documentId).Publish(output, http.RequestAborted));
        });
    }
}
