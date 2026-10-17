using Fiddler.Formats.Catalog;
using FiddlerWebApp.Services.Users;
using Microsoft.AspNetCore.Antiforgery;

namespace FiddlerWebApp.Api;

public static class FiddlerApi
{
    public static void MapFiddlerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<ApiGuard>().WithTags("Fiddler API v1");
        api.MapGet("/me", (HttpContext http) => Results.Ok(http.RequestServices.GetRequiredService<UserSession>().RequireUser()))
            .WithSummary("Get the current API identity.");
        api.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
        }).WithSummary("Get the CSRF token required for cookie-authenticated writes.");
        api.MapGet("/workspaces", (HttpContext http, ApiWorkspaceRegistry registry) => Results.Ok(registry.List(http.User)))
            .WithSummary("List this login's API workspaces.");
        api.MapPost("/workspaces", (CreateWorkspaceRequest request, HttpContext http, ApiWorkspaceRegistry registry) =>
        {
            var workspace = registry.Create(http.User, request.Directory);
            try { http.Response.Headers.ETag = workspace.ETag; return Results.Created($"/api/v1/workspaces/{workspace.Id}", workspace.Info()); }
            finally { registry.ReleaseUse(workspace); }
        }).WithSummary("Open a server directory in an independent API workspace.").Produces<WorkspaceInfo>(201);
        api.MapGet("/workspaces/{workspaceId:guid}", (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(w.Info())))
            .WithSummary("Get workspace state and its current ETag.").Produces<WorkspaceInfo>();
        api.MapDelete("/workspaces/{workspaceId:guid}", (Guid workspaceId, bool? discardDrafts, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w =>
            {
                if (w.Assets.Gumps?.HasChanges == true && discardDrafts != true) throw new ApiException(409, "The workspace has drafts. Publish them or explicitly set discardDrafts=true.");
                registry.Close(w); return Results.NoContent();
            })).WithSummary("Close a workspace and release its readers and leases.");
        api.MapPost("/workspaces/{workspaceId:guid}/heartbeat", (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(w.Info())))
            .WithSummary("Keep an API workspace and its active edit leases alive.");
        api.MapGet("/workspaces/{workspaceId:guid}/files", (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry, ClientDirectoryScanner scanner) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(scanner.Scan(w.Directory, http.RequestAborted))))
            .WithSummary("Scan the workspace directory for missing, invalid and alternative files.");
        api.MapPost("/workspaces/{workspaceId:guid}/refresh", (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w => { w.Assets.RefreshShared(); return Results.Ok(new { sharedRevision = w.Assets.SharedRevision }); }))
            .WithSummary("Refresh the shared Gump base while preserving private drafts.");
        api.MapGet("/workspaces/{workspaceId:guid}/leases", (Guid workspaceId, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Read(http, registry, workspaceId, w => Results.Ok(w.Assets.EditLeases().Select(l => new
            { kind = l.Asset.Kind, id = l.Asset.Id, userName = l.UserName, own = l.SessionId == w.User.SessionId, expires = l.Expires, saving = l.Saving }))))
            .WithSummary("List Gump edit leases without exposing fencing tokens.");
        api.MapPost("/workspaces/{workspaceId:guid}/leases/gumps/{id:int}", (Guid workspaceId, int id, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w => { var lease = w.Assets.BeginEdit(id); return Results.Ok(new { id, owner = lease.UserName, expires = lease.Expires }); }))
            .WithSummary("Reserve a Gump ID for this API workspace.");
        api.MapDelete("/workspaces/{workspaceId:guid}/leases/gumps/{id:int}", (Guid workspaceId, int id, HttpContext http, ApiWorkspaceRegistry registry) =>
            ApiOperations.Write(http, registry, workspaceId, w => { w.Assets.ReleaseEdit(id); return Results.NoContent(); }))
            .WithSummary("Release an ID that has no private draft.");
        GumpEndpoints.Map(api);
        WorldEndpoints.Map(api);
        AnimationEndpoints.Map(api);
    }
}
