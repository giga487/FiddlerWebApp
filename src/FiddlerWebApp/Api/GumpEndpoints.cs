using System.Buffers;
using Fiddler.Formats;
using Fiddler.Formats.Gumps;
using Fiddler.Graphics;
using Fiddler.Graphics.Gumps;

namespace FiddlerWebApp.Api;

public sealed record PageResult<T>(int Offset, int Limit, int Total, IReadOnlyList<T> Items);
public sealed record GumpListItem(int Id, int EncodedBytes, bool Draft);
public sealed record GumpInfo(int Id, int Width, int Height, int EncodedBytes, bool Draft, long SharedRevision);
public sealed record CopyGumpRequest(string ReferencePath, int ReferenceId, string? ReferenceIndex = null);
public sealed record CompareGumpsRequest(string ReferencePath, string? ReferenceIndex = null, bool IncludeEqual = false, int Offset = 0, int Limit = 24);
public sealed record PublishGumpsRequest(string? OutputDirectory = null);

public static class GumpEndpoints
{
    private const string Root = "/workspaces/{workspaceId:guid}";
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(Root + "/gumps", (Guid workspaceId, int? offset, int? limit, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var page = ApiOperations.Page(offset, limit); var document = Gumps(w);
                return Results.Ok(new PageResult<GumpListItem>(page.Offset, page.Limit, document.Entries.Count,
                    document.Entries.Skip(page.Offset).Take(page.Limit).Select(e => new GumpListItem(e.Id, e.Length, document.IsModified(e.Id))).ToArray()));
            })).WithSummary("List Gump IDs without decoding the entire archive.").Produces<PageResult<GumpListItem>>();
        api.MapGet(Root + "/gumps/{id:int}", (Guid workspaceId, int id, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(Info(w, id))))
            .WithSummary("Get dimensions and draft status for a Gump.").Produces<GumpInfo>();
        api.MapGet(Root + "/gumps/{id:int}/image", (Guid workspaceId, int id, int? scale, bool? thumbnail, HttpContext http, ApiWorkspaceRegistry registry, SkiaAssetRenderer renderer) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var image = Gumps(w).Read(id);
                return Results.File(thumbnail == true ? renderer.RenderThumbnail(image) : renderer.RenderPng(image, scale ?? 1), "image/png");
            })).WithSummary("Render a Gump PNG or thumbnail on demand.").Produces(200, contentType: "image/png");
        api.MapPut(Root + "/gumps/{id:int}/image", (Guid workspaceId, int id, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Run(http, registry, workspaceId, true, async w =>
            {
                if (!w.Assets.OwnsEdit(id)) throw new ApiException(409, "Reserve this Gump ID before importing an image.");
                if (!string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "image/png", StringComparison.OrdinalIgnoreCase)) throw new ApiException(415, "Send a raw PNG body with Content-Type: image/png.");
                var bytes = await ReadPng(http);
                w.Assets.PutGump(id, GumpImageImporter.ImportPng(bytes));
                return Results.Ok(Info(w, id));
            })).WithSummary("Import a raw PNG into a reserved Gump ID.").WithDescription("Send PNG bytes, not JSON or multipart. Requires an edit lease, antiforgery header and If-Match.")
            .Accepts<Stream>("image/png").Produces<GumpInfo>();
        api.MapPost(Root + "/gumps/{id:int}/copy", (Guid workspaceId, int id, CopyGumpRequest request, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w =>
            {
                if (!w.Assets.OwnsEdit(id)) throw new ApiException(409, "Reserve the target Gump ID before copying.");
                using var reference = GumpArchive.Open(request.ReferencePath, request.ReferenceIndex);
                w.Assets.PutGump(id, reference.ReadRecord(request.ReferenceId));
                return Results.Ok(Info(w, id));
            })).WithSummary("Copy a Gump from a server-side MUL/UOP archive into a reserved target ID.").Produces<GumpInfo>();
        api.MapDelete(Root + "/gumps/{id:int}/draft", (Guid workspaceId, int id, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w => { Gumps(w); w.Assets.UndoGump(id); return Results.NoContent(); }))
            .WithSummary("Undo this workspace's draft for one ID and release its lease.");
        api.MapDelete(Root + "/drafts", (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w => { w.Assets.DiscardGumps(); return Results.NoContent(); }))
            .WithSummary("Discard all private Gump drafts and release their leases.");
        api.MapPost(Root + "/gumps/compare", (Guid workspaceId, CompareGumpsRequest request, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Run(http, registry, workspaceId, false, async w =>
            {
                var page = ApiOperations.Page(request.Offset, request.Limit);
                using var reference = GumpArchive.Open(request.ReferencePath, request.ReferenceIndex);
                var rows = await Task.Run(() => GumpComparer.Compare(Gumps(w), reference, http.RequestAborted), http.RequestAborted);
                var filtered = rows.Where(r => request.IncludeEqual || r.Difference != GumpDifference.Equal).ToArray();
                return Results.Ok(new
                {
                    sharedRevision = w.Assets.SharedRevision,
                    summary = Enum.GetValues<GumpDifference>().ToDictionary(s => s.ToString(), s => rows.Count(r => r.Difference == s)),
                    results = new PageResult<GumpComparison>(page.Offset, page.Limit, filtered.Length, filtered.Skip(page.Offset).Take(page.Limit).ToArray())
                });
            })).WithSummary("Compare decoded Gumps against a server-side MUL/UOP archive.")
            .WithDescription("Comparison is cancellable through request cancellation. Only the requested result page is returned.");
        api.MapPost(Root + "/publish", (Guid workspaceId, PublishGumpsRequest request, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Run(http, registry, workspaceId, true, async w => Results.Ok(await w.Assets.SaveSharedAsync(
                request.OutputDirectory ?? Path.Combine(w.Directory, "FiddlerExports"), http.RequestAborted))))
            .WithSummary("Merge private drafts into a new complete shared MUL/IDX version.");
        api.MapGet(Root + "/art/{kind}", (Guid workspaceId, string kind, int? offset, int? limit, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var page = ApiOperations.Page(offset, limit); var entries = w.Assets.Entries(Kind(kind));
                return Results.Ok(new PageResult<MulEntry>(page.Offset, page.Limit, entries.Count, entries.Skip(page.Offset).Take(page.Limit).ToArray()));
            })).WithSummary("List land or item IDs from the MUL/IDX Art catalog.");
        api.MapGet(Root + "/art/{kind}/{id:int}/image", (Guid workspaceId, string kind, int id, HttpContext http, ApiWorkspaceRegistry registry, SkiaAssetRenderer renderer) =>
            ApiOperations.Read(http, registry, workspaceId, w =>
            {
                var entry = w.Assets.Entries(Kind(kind)).FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException("Art ID not found in the MUL/IDX catalog.");
                return Results.File(renderer.RenderPng(w.Assets.Read(entry)), "image/png");
            })).WithSummary("Render a land or item PNG from the MUL/IDX Art catalog.").Produces(200, contentType: "image/png");
    }
    private static AssetKind Kind(string kind) => kind.ToLowerInvariant() switch
    { "land" => AssetKind.Land, "items" => AssetKind.Item, _ => throw new ArgumentException("Art kind must be land or items.") };
    private static GumpDocument Gumps(ApiWorkspace workspace) => workspace.Assets.Gumps ?? throw new KeyNotFoundException("No Gump archive is available in this workspace.");
    private static GumpInfo Info(ApiWorkspace workspace, int id)
    {
        var document = Gumps(workspace); var record = document.ReadRecord(id); _ = record.Decode();
        return new(id, record.Width, record.Height, record.Data.Length, document.IsModified(id), workspace.Assets.SharedRevision);
    }
    private static async Task<byte[]> ReadPng(HttpContext http)
    {
        if (http.Request.ContentLength > GumpImageImporter.MaxUploadBytes) throw new ApiException(413, "PNG upload exceeds 8 MiB.");
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) != 0)
            {
                if (output.Length + count > GumpImageImporter.MaxUploadBytes) throw new ApiException(413, "PNG upload exceeds 8 MiB.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
