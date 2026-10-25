using UOAS.Application.Atlas;

namespace UOAS.Backend.Endpoints;

public static class AtlasEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/atlas/maps", (AtlasService atlas) => atlas.Catalog());
        api.MapGet("/atlas/{library}/{facet:int}/thumbnail", async (string library, int facet, string revision, AtlasService atlas, HttpContext http) => Results.File(await atlas.Thumbnail(library, facet, revision, http.RequestAborted), "image/png"));
        api.MapGet("/atlas/{library}/{facet:int}/tiles/{scale:int}/{x:int}/{y:int}", async (string library, int facet, string revision, int scale, int x, int y, AtlasService atlas, HttpContext http) => Results.File(await atlas.Tile(library, facet, revision, scale, x, y, http.RequestAborted), "image/png"));
    }
}
