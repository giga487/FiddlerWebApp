using Fiddler.Plugin.Abstractions;
using UOAS.Application.Collaboration;
using UOAS.Contracts;

namespace UOAS.Backend.Endpoints;

public static class PluginEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/plugins", (PluginPackages packages) => packages.List());
        api.MapPost("/plugins", (InstallPluginRequest request, PluginPackages packages) => packages.Install(request));
        api.MapPost("/plugins/{key}/activate", (string key, PluginPackages packages) => packages.Activate(key));
        api.MapPost("/plugins/{key}/deactivate", async (string key, PluginPackages packages) => { await packages.Deactivate(key); return Results.NoContent(); });
        api.MapDelete("/plugins/{key}", async (string key, PluginPackages packages) => { await packages.Deactivate(key, remove: true); return Results.NoContent(); });
        api.MapPost("/plugins/{key}/execute", async (string key, ExecutePluginRequest request, HttpContext http, PluginPackages packages) =>
        {
            var image = CollaborationEndpoints.Workspace(http, request.WorkspaceId).Read(request.Kind, request.Id);
            return await packages.Execute(key, request.Command, new PluginAsset(request.Kind.ToString(), request.Id, image.Width, image.Height, image.Pixels.ToArray()), http.RequestAborted);
        });
    }
}
