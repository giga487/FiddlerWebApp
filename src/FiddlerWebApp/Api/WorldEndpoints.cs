using Fiddler.Formats.Maps;
using Fiddler.Formats.Multis;
using Fiddler.Graphics.World;

namespace FiddlerWebApp.Api;

public static class WorldEndpoints
{
    private const string Root = "/workspaces/{workspaceId:guid}";
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(Root + "/multis", (Guid workspaceId, string? source, int? rowSize, int? offset, int? limit, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var page = ApiOperations.Page(offset, limit); var multi = Multis(w, source, rowSize);
                return Results.Ok(new { format = multi.Format, results = new PageResult<int>(page.Offset, page.Limit, multi.Ids.Count, multi.Ids.Skip(page.Offset).Take(page.Limit).ToArray()) });
            })).WithSummary("List Multi IDs from MUL/IDX or MultiCollection UOP.");
        api.MapGet(Root + "/multis/{id:int}", (Guid workspaceId, int id, string? source, int? rowSize, int? offset, int? limit, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var page = ApiOperations.Page(offset, limit); var multi = Multis(w, source, rowSize); var rows = multi.Read(id);
                return Results.Ok(new { id, format = multi.Format, components = new PageResult<MultiComponent>(page.Offset, page.Limit, rows.Count, rows.Skip(page.Offset).Take(page.Limit).ToArray()) });
            })).WithSummary("Inspect a paginated list of Multi components and signed X/Y/Z coordinates.");
        api.MapGet(Root + "/multis/{id:int}/image", (Guid workspaceId, int id, string? source, int? rowSize, int? maxZ, bool? includeInvisible, HttpContext http, ApiWorkspaceRegistry registry, IsometricSceneRenderer renderer) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var multi = Multis(w, source, rowSize);
                var tiles = multi.Read(id).Where(t => t.Z <= (maxZ ?? short.MaxValue) && (includeInvisible == true || t.Flags != 0))
                    .Select(t => new WorldTile(t.Index, t.ItemId, t.X, t.Y, t.Z, false, Flags: t.Flags));
                var scene = renderer.Render(tiles, w.World.ReadArt);
                http.Response.Headers["X-Fiddler-Warning-Count"] = scene.Warnings.Count.ToString();
                return Results.File(scene.Png, "image/png");
            })).WithSummary("Render an isometric Multi PNG using the shared SkiaSharp renderer.").Produces(200, contentType: "image/png");
        api.MapGet(Root + "/maps/{facet:int}", (Guid workspaceId, int facet, string? source, bool? includeStatics, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var map = Map(w, facet, source, includeStatics); var size = MapArchive.DetectSize(facet, map.BlockCount);
                return Results.Ok(new { facet, format = map.Format, map.BlockCount, map.HasStatics, width = size?.Width, height = size?.Height, customDimensionsRequired = size is null });
            })).WithSummary("Inspect map format, block count, statics and detected dimensions.");
        api.MapGet(Root + "/maps/{facet:int}/region", (Guid workspaceId, int facet, int x, int y, int? size, int? width, int? height, string? source, bool? includeStatics, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(Region(w, facet, x, y, size, width, height, source, includeStatics))))
            .WithSummary("Read a bounded terrain/static region, up to 16 by 16 tiles.").Produces<MapRegion>();
        api.MapGet(Root + "/maps/{facet:int}/region/image", (Guid workspaceId, int facet, int x, int y, int? size, int? width, int? height, string? source, bool? includeStatics, HttpContext http, ApiWorkspaceRegistry registry, IsometricSceneRenderer renderer) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var region = Region(w, facet, x, y, size, width, height, source, includeStatics);
                var tiles = region.Land.Select((t, i) => new WorldTile(i, t.TileId, t.X, t.Y, t.Z, true)).ToList();
                tiles.AddRange(region.Statics.Select((t, i) => new WorldTile(i + region.Land.Count, t.ItemId, t.X, t.Y, t.Z, false, t.Hue)));
                var scene = renderer.Render(tiles, w.World.ReadArt);
                http.Response.Headers["X-Fiddler-Warning-Count"] = scene.Warnings.Count.ToString();
                return Results.File(scene.Png, "image/png");
            })).WithSummary("Render a bounded map region as an isometric PNG.").Produces(200, contentType: "image/png");
    }
    private static MultiArchive Multis(ApiWorkspace workspace, string? source, int? rowSize)
    { workspace.World.OpenMultis(workspace.Directory, ApiOperations.Source(source), rowSize ?? 16); return workspace.World.Multis!; }
    private static MapArchive Map(ApiWorkspace workspace, int facet, string? source, bool? includeStatics)
    { workspace.World.OpenMap(workspace.Directory, facet, ApiOperations.Source(source), includeStatics ?? true); return workspace.World.Map!; }
    private static MapRegion Region(ApiWorkspace workspace, int facet, int x, int y, int? size, int? width, int? height, string? source, bool? includeStatics)
    {
        var map = Map(workspace, facet, source, includeStatics);
        var detected = MapArchive.DetectSize(facet, map.BlockCount);
        if ((width is null) != (height is null)) throw new ArgumentException("Provide both width and height for a custom map.");
        int w = width ?? detected?.Width ?? throw new ArgumentException("This custom map requires explicit width and height in tiles.");
        int h = height ?? detected!.Value.Height;
        return map.ReadRegion(w, h, x, y, size ?? 8);
    }
}
