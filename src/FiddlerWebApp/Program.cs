using FiddlerWebApp.Components;
using FiddlerWebApp.Services;
using Fiddler.Graphics;
using Fiddler.Graphics.Animations;
using Fiddler.Formats.Catalog;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddScoped<MulWorkspace>();
builder.Services.AddScoped<ColorStudioWorkspace>();
builder.Services.AddScoped<AnimationWorkspace>();
builder.Services.AddScoped<WorldAssetWorkspace>();
builder.Services.AddSingleton<Fiddler.Graphics.World.IsometricSceneRenderer>();
builder.Services.AddScoped<Fiddler.Graphics.Gumps.GumpComposition>();
builder.Services.AddSingleton<Fiddler.Graphics.Gumps.GumpCompositionRenderer>();
builder.Services.AddSingleton<SkiaAssetRenderer>();
builder.Services.AddSingleton<AnimationRenderer>();
builder.Services.AddSingleton<ClientDirectoryScanner>();
builder.Services.AddSingleton(new ClientDirectorySettings(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "client-directory.json"),
    builder.Configuration["Mul:Directory"] ?? ""));

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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
