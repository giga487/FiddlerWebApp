using FiddlerWebApp.Components;
using FiddlerWebApp.Services;
using Fiddler.Graphics;
using Fiddler.Graphics.Animations;
using Fiddler.Formats.Catalog;
using Microsoft.FluentUI.AspNetCore.Components;
using FiddlerWebApp.Services.Users;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FiddlerWebApp.Api;
using FiddlerWebApp.Services.Plugins;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(ApiDescription.Configure);
builder.Services.AddSingleton<ApiWorkspaceRegistry>();
builder.Services.AddHostedService<ApiWorkspaceReaper>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new PluginRegistry(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "plugins"),
    builder.Configuration, services.GetRequiredService<ILogger<PluginRegistry>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<PluginRegistry>());
builder.Services.AddSingleton<ResourceLeaseManager>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddScoped<UserSession>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, UserCircuitHandler>();
builder.Services.AddSingleton(services => new SharedGumpStore(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "shared-gumps"), services.GetRequiredService<ResourceLeaseManager>()));
builder.AddWorkspaceAuthentication();
builder.Services.AddScoped<MulWorkspace>();
builder.Services.AddScoped<ColorStudioWorkspace>();
builder.Services.AddScoped<ClientResourceWorkspace>();
builder.Services.AddScoped<AnimationWorkspace>();
builder.Services.AddScoped<WorldAssetWorkspace>();
builder.Services.AddSingleton<Fiddler.Graphics.World.IsometricSceneRenderer>();
builder.Services.AddScoped<Fiddler.Graphics.Gumps.GumpComposition>();
builder.Services.AddSingleton<Fiddler.Graphics.Gumps.GumpCompositionRenderer>();
builder.Services.AddSingleton<SkiaAssetRenderer>();
builder.Services.AddSingleton<AnimationRenderer>();
builder.Services.AddSingleton<ClientDirectoryScanner>();
builder.Services.AddScoped(services =>
{
    var user = services.GetRequiredService<UserSession>().RequireUser();
    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id)));
    return new ClientDirectorySettings(
        Path.Combine(builder.Environment.ContentRootPath, "App_Data", "users", key, "client-directory.json"),
        builder.Configuration["Mul:Directory"] ?? "");
});

var app = builder.Build();

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en-US")
    .AddSupportedCultures("en-US")
    .AddSupportedUICultures("en-US"));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), branch =>
    branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), branch =>
    branch.UseStatusCodePages(context => ApiGuard.Problem(context.HttpContext, context.HttpContext.Response.StatusCode,
        "The API request could not be processed. Check the route, parameters and content type.").ExecuteAsync(context.HttpContext)));
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted)
    {
        int status = error is BadHttpRequestException bad ? bad.StatusCode : 500;
        if (status == 500) context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FiddlerApi").LogError(error, "API request failed");
        context.Response.Clear();
        context.Response.Headers.CacheControl = "no-store";
        await ApiGuard.Problem(context, status, status == 500 ? "The API request failed. Use the trace ID to inspect server logs." : error.Message).ExecuteAsync(context);
    }
});
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();
app.MapWorkspaceAuthentication();
app.MapFiddlerApi();
app.MapOpenApi("/api/openapi/{documentName}.json").RequireAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
