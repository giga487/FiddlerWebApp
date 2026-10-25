using Fiddler.Formats.Maps;
using Fiddler.Graphics.Animations;
using Fiddler.Graphics.World;
using FiddlerWebApp.Services.Diagnostics;
using Serilog.Events;

namespace UOAS.Backend.Endpoints;

public static class ToolEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/diagnostics", (int? level, string? category, string? message, bool? exceptionsOnly, DateTimeOffset? from, DateTimeOffset? until, long? afterSequence, int? limit, DiagnosticLogStore logs) =>
        {
            if (from > until) throw new ArgumentException("The start time must precede the end time.");
            return logs.Query(new((LogEventLevel)Math.Clamp(level ?? 0, 0, 5), category ?? "", message ?? "", exceptionsOnly ?? false, from, until, afterSequence ?? 0), limit ?? 200);
        });
        api.MapGet("/diagnostics/status", (DiagnosticLogStore logs, DiagnosticLogControl control) => new { latestSequence = logs.LatestSequence, capacity = logs.Capacity, captureLevel = (int)control.LevelSwitch.MinimumLevel });
        api.MapPost("/diagnostics/level/{level:int}", (int level, DiagnosticLogControl control) =>
        {
            if (level < 0 || level > 5) throw new ArgumentException("Choose a capture level from 0 to 5.");
            control.LevelSwitch.MinimumLevel = (LogEventLevel)level; return Results.NoContent();
        });
        const string root = "/workspaces/{workspaceId:guid}/tools";
        api.MapGet(root + "/hue-mode/{kind}/{id:int}", (Guid workspaceId, UOAS.Contracts.ResourceKind kind, int id, HttpContext http) => new { partialHue = CollaborationEndpoints.Workspace(http, workspaceId).AutomaticPartialHue(kind, id) });
        api.MapGet(root + "/multi", (Guid workspaceId, int id, int? rowSize, HttpContext http, IsometricSceneRenderer renderer) =>
        {
            var workspace = CollaborationEndpoints.Workspace(http, workspaceId);
            workspace.OpenMulti(rowSize ?? 16);
            var tiles = workspace.World.Multis!.Read(id).Where(tile => tile.Flags != 0)
                .Select(tile => new WorldTile(tile.Index, tile.ItemId, tile.X, tile.Y, tile.Z, false, Flags: tile.Flags));
            return Results.File(renderer.Render(tiles, workspace.World.ReadArt).Png, "image/png");
        });
        api.MapGet(root + "/animations", (Guid workspaceId, HttpContext http) =>
        {
            var workspace = CollaborationEndpoints.Workspace(http, workspaceId);
            if (workspace.Animations.Readers.Count == 0) workspace.Animations.Open(workspace.Directory);
            return workspace.Animations.Readers.Select(reader => new { file = reader.FileType, name = reader.FileName, bodies = reader.Bodies.Count });
        });
        api.MapGet(root + "/animations/{file:int}/bodies", (Guid workspaceId, int file, string? category, int? id, int? offset, int? limit, HttpContext http) =>
        {
            var workspace = CollaborationEndpoints.Workspace(http, workspaceId);
            if (workspace.Animations.Readers.Count == 0) workspace.Animations.Open(workspace.Directory);
            var reader = workspace.Animations.Readers.FirstOrDefault(reader => reader.FileType == file) ?? throw new KeyNotFoundException("Animation archive not found.");
            int start = offset ?? 0, count = limit ?? 24;
            if (start < 0 || count < 1 || count > 100) throw new ArgumentException("Choose a page size from 1 to 100.");
            var bodies = reader.Bodies.Where(body => (string.IsNullOrEmpty(category) || body.Type.ToString().Equals(category, StringComparison.OrdinalIgnoreCase)) && (id is null || body.Id == id)).ToArray();
            return Results.Ok(new { total = bodies.Length, items = bodies.Skip(start).Take(count).Select(body => new UOAS.Contracts.AnimationBodyInfo(body.Id, body.Type.ToString())) });
        });
        api.MapGet(root + "/animations/{file:int}/bodies/{body:int}/actions", (Guid workspaceId, int file, int body, HttpContext http) =>
        {
            var workspace = CollaborationEndpoints.Workspace(http, workspaceId);
            if (workspace.Animations.Readers.Count == 0) workspace.Animations.Open(workspace.Directory);
            var reader = workspace.Animations.Readers.FirstOrDefault(reader => reader.FileType == file) ?? throw new KeyNotFoundException("Animation archive not found.");
            return reader.Actions(body).Select(action => new UOAS.Contracts.AnimationActionInfo(action.Id, action.Name, Enumerable.Range(0, 8).Where(direction => reader.HasSequence(body, action.Id, direction)).ToArray()));
        });
        api.MapGet(root + "/animations/{file:int}/{body:int}/{action:int}/{direction:int}", (Guid workspaceId, int file, int body, int action, int direction, HttpContext http, AnimationRenderer renderer) =>
        {
            var workspace = CollaborationEndpoints.Workspace(http, workspaceId);
            if (workspace.Animations.Readers.Count == 0) workspace.Animations.Open(workspace.Directory);
            var reader = workspace.Animations.Readers.FirstOrDefault(reader => reader.FileType == file) ?? throw new KeyNotFoundException("Animation archive not found.");
            var atlas = renderer.RenderAtlas(reader.Read(body, action, direction));
            return Results.Ok(new { atlas.FrameWidth, atlas.FrameHeight, atlas.FrameCount, atlas.Columns, image = Convert.ToBase64String(atlas.Png) });
        });
    }
}
