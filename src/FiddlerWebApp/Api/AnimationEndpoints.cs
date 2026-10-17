using Fiddler.Formats.Animations;
using Fiddler.Graphics.Animations;

namespace FiddlerWebApp.Api;

public static class AnimationEndpoints
{
    private const string Root = "/workspaces/{workspaceId:guid}/animations";
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(Root, (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                w.Animations.Open(w.Directory);
                return Results.Ok(new { archives = w.Animations.Readers.Select(r => new { file = r.FileType, r.FileName, bodies = r.Bodies.Count }), w.Animations.Warnings });
            })).WithSummary("List available animation MUL archives and warnings.");
        api.MapGet(Root + "/{file:int}/bodies", (Guid workspaceId, int file, int? offset, int? limit, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var page = ApiOperations.Page(offset, limit); var reader = Reader(w, file);
                return Results.Ok(new PageResult<AnimationBody>(page.Offset, page.Limit, reader.Bodies.Count, reader.Bodies.Skip(page.Offset).Take(page.Limit).ToArray()));
            })).WithSummary("List physical animation body IDs with pagination.");
        api.MapGet(Root + "/{file:int}/bodies/{body:int}/actions", (Guid workspaceId, int file, int body, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(Reader(w, file).Actions(body))))
            .WithSummary("List the available actions for a body.");
        api.MapGet(Root + "/{file:int}/bodies/{body:int}/actions/{action:int}/directions/{direction:int}", (Guid workspaceId, int file, int body, int action, int direction, HttpContext http, ApiWorkspaceRegistry registry, AnimationRenderer renderer) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var atlas = renderer.RenderAtlas(Reader(w, file).Read(body, action, direction));
                return Results.Ok(new { atlas.FrameWidth, atlas.FrameHeight, atlas.FrameCount, atlas.Columns, defaultDelayMs = 150,
                    image = $"/api/v1/workspaces/{workspaceId}/animations/{file}/bodies/{body}/actions/{action}/directions/{direction}/image" });
            })).WithSummary("Get stable-anchor atlas dimensions and frame count for a sequence.");
        api.MapGet(Root + "/{file:int}/bodies/{body:int}/actions/{action:int}/directions/{direction:int}/image", (Guid workspaceId, int file, int body, int action, int direction, HttpContext http, ApiWorkspaceRegistry registry, AnimationRenderer renderer) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.File(renderer.RenderAtlas(Reader(w, file).Read(body, action, direction)).Png, "image/png")))
            .WithSummary("Render an animation atlas PNG for client-side playback.").Produces(200, contentType: "image/png");
    }
    private static AnimationMulReader Reader(ApiWorkspace workspace, int file)
    {
        if (workspace.Animations.Readers.Count == 0) workspace.Animations.Open(workspace.Directory);
        return workspace.Animations.Readers.FirstOrDefault(r => r.FileType == file) ?? throw new KeyNotFoundException("Animation archive not found.");
    }
}
