using Fiddler.Graphics;
using Fiddler.Formats.Catalog;
using FiddlerWebApp.Services.Diagnostics;
using FiddlerWebApp.Services.Users;
using Serilog;
using UOAS.Application.Collaboration;
using UOAS.Backend.Endpoints;

namespace UOAS.Backend;

public partial class BackendProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddDiagnosticsLogging();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ResourceLeaseManager>();
        builder.Services.AddSingleton(services => new UOAS.Application.Regions.RegionStore(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "regions"), services.GetRequiredService<ResourceLeaseManager>()));
        builder.Services.AddSingleton<SkiaAssetRenderer>();
        builder.Services.AddSingleton<Fiddler.Graphics.World.IsometricSceneRenderer>();
        builder.Services.AddSingleton<Fiddler.Graphics.Animations.AnimationRenderer>();
        builder.Services.AddSingleton<ClientDirectoryScanner>();
        builder.Services.AddSingleton(new PluginPackages(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "packages"), builder.Configuration));
        builder.Services.AddSingleton(services => new SharedGumpStore(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "gumps"), services.GetRequiredService<ResourceLeaseManager>()));
        builder.Services.AddSingleton(services => new SharedArtStore(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "art"), services.GetRequiredService<ResourceLeaseManager>()));
        builder.Services.AddSingleton(services => new SharedDocumentStore(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "documents"), services.GetRequiredService<ResourceLeaseManager>()));
        builder.Services.AddOpenApi();
        builder.Services.AddSingleton<CollaborationSessions>();
        builder.Services.AddHostedService<CollaborationReaper>();
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(builder.Configuration.GetSection("Frontend:Origins").Get<string[]>() ?? ["http://localhost:5178", "http://127.0.0.1:5178"])
                .WithMethods("GET", "POST", "PUT", "DELETE").WithHeaders("Content-Type", "X-UOAS-Session").WithExposedHeaders("ETag")));
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
        var app = builder.Build();
        app.UseCors();
        app.UseSerilogRequestLogging();
        app.MapGet("/health", () => Results.Ok(new { service = "UOAS.Backend", status = "ready" }));
        app.MapCollaboration();
        app.MapOpenApi("/api/openapi/{documentName}.json");
        await app.RunAsync();
    }
}
