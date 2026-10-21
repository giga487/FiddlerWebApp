using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;
using Fiddler.Formats.Metadata;
using Fiddler.Graphics;
using FiddlerWebApp.Components.Pages;
using FiddlerWebApp.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Fiddler.Tests;

public sealed class ResourceEditorRenderingTests
{
    [Fact]
    public async Task CatalogShowsSelectableEmptyGumpSlotsWithoutDecodeErrors()
    {
        using var files = new ClientResourceEditingTests.Files();
        var record = GumpEncoder.Encode(DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        files.Write("gumpart.mul", record.Data);
        using (var index = new BinaryWriter(File.Create(Path.Combine(files.Root, "gumpidx.mul"))))
        {
            for (int id = 0; id < 4; id++)
            {
                index.Write(id == 1 ? 0 : -1);
                index.Write(id == 1 ? record.Data.Length : 0);
                index.Write(id == 1 ? (1 << 16) | 1 : 0);
            }
        }
        using var workspace = new MulWorkspace();
        workspace.Open(files.Root);
        Assert.Equal(new[] { 0, 1, 2, 3 }, workspace.CatalogEntries(AssetKind.Gump).Select(entry => entry.Id));
        Assert.Equal(1, Assert.Single(workspace.CatalogEntries(AssetKind.Gump, includeEmpty: false)).Id);
        string html = await Render(typeof(Home), files.Root, new ClientResourceWorkspace(), workspace);
        Assert.Contains("Show empty slots", html);
        Assert.Contains("Open Gump ID 0x0000", html);
        Assert.Contains("Open Gump ID 0x0003", html);
        Assert.Equal(3, html.Split("Empty slot").Length - 1);
        Assert.Contains("data:image/png;base64,", html);
        Assert.DoesNotContain("Invalid record", html);
        workspace.Gumps!.Put(3, record);
        Assert.True(workspace.CatalogEntries(AssetKind.Gump)[3].Length > 0);
        workspace.Gumps.Undo(3);
        Assert.Equal(0, workspace.CatalogEntries(AssetKind.Gump)[3].Length);
    }

    [Fact]
    public void ArtCatalogKeepsLandAndItemSlotBoundariesAndDraftRemovals()
    {
        using var files = new ClientResourceEditingTests.Files();
        byte[] art = ArtEncoder.Item(DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        files.Write("art.mul", art);
        using (var index = new BinaryWriter(File.Create(Path.Combine(files.Root, "artidx.mul"))))
        {
            for (int slot = 0; slot < 0x4002; slot++)
            {
                index.Write(slot == 0x4000 ? 0 : -1);
                index.Write(slot == 0x4000 ? art.Length : 0);
                index.Write(0);
            }
        }
        using var workspace = new MulWorkspace();
        workspace.Open(files.Root);
        Assert.Equal(0x4000, workspace.CatalogEntries(AssetKind.Land).Count);
        var items = workspace.CatalogEntries(AssetKind.Item);
        Assert.Equal(new[] { 0, 1 }, items.Select(entry => entry.Id));
        Assert.True(items[0].Length > 0);
        Assert.Equal(0, items[1].Length);
        workspace.Art!.Remove(AssetKind.Item, 0);
        Assert.All(workspace.CatalogEntries(AssetKind.Item), entry => Assert.Equal(0, entry.Length));
        Assert.Empty(workspace.CatalogEntries(AssetKind.Item, includeEmpty: false));
        workspace.Art.Put(AssetKind.Item, 3, DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        Assert.Equal(4, workspace.CatalogEntries(AssetKind.Item).Count);
        Assert.Equal(0x4000, workspace.CatalogEntries(AssetKind.Land).Count);
    }

    [Theory]
    [InlineData(typeof(Fonts), "No fonts.mul")]
    [InlineData(typeof(Cliloc), "No cliloc.*")]
    [InlineData(typeof(TileData), "Open tiledata.mul")]
    [InlineData(typeof(ArtManagement), "Requires art.mul")]
    public async Task MissingFilesShowUsableEmptyPages(Type component, string expected)
    {
        using var files = new ClientResourceEditingTests.Files();
        var resources = new ClientResourceWorkspace();
        using var workspace = new MulWorkspace();
        string html = await Render(component, files.Root, resources, workspace);
        Assert.Contains(expected, html);
        Assert.Contains("<h1>", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    [Theory]
    [InlineData(typeof(Fonts), "Download glyph PNG")]
    [InlineData(typeof(Cliloc), "String editor")]
    [InlineData(typeof(SelectedTileData), "PartialHue")]
    [InlineData(typeof(ArtManagement), "Download PNG")]
    public async Task LoadedResourcePagesRenderEditorsAndRealPreviews(Type component, string expected)
    {
        using var files = new ClientResourceEditingTests.Files();
        files.Write("fonts.mul", ClientResourceEditingTests.AsciiBytes());
        files.Write("cliloc.enu", ClientResourceEditingTests.ClilocBytes(new Fiddler.Formats.Localization.ClilocEntry(1, 0, "Hello <script>alert(1)</script>")));
        files.Write("tiledata.mul", ClientResourceEditingTests.TileDataBytes(TileDataLayout.Extended64));
        byte[] art = ArtEncoder.Item(DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        files.Write("art.mul", art);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(files.Root, "artidx.mul"))))
            for (int slot = 0; slot <= 0x4000; slot++) { writer.Write(slot == 0x4000 ? 0 : -1); writer.Write(slot == 0x4000 ? art.Length : 0); writer.Write(0); }
        var resources = new ClientResourceWorkspace();
        resources.OpenFont(files.Root, "fonts.mul"); resources.OpenCliloc(files.Root, "cliloc.enu"); resources.OpenTileData(files.Root);
        using var workspace = new MulWorkspace(); workspace.Open(files.Root);
        string html = await Render(component, files.Root, resources, workspace);
        Assert.Contains(expected, html);
        Assert.Contains("Export new", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("role=\"alert\"", html);
        if (component == typeof(Fonts) || component == typeof(ArtManagement)) Assert.Contains("data:image/png;base64,", html);
    }

    public sealed class SelectedTileData : TileData
    {
        protected override void OnInitialized()
        { RequestedKind = "Item"; RequestedId = "3"; base.OnInitialized(); }
    }

    private static async Task<string> Render(Type component, string folder, ClientResourceWorkspace resources, MulWorkspace workspace)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddFluentUIComponents();
        services.AddSingleton(resources); services.AddSingleton(workspace); services.AddSingleton<SkiaAssetRenderer>();
        services.AddSingleton(new ClientDirectorySettings(Path.Combine(folder, "settings.json"), folder));
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync(component,
            ParameterView.Empty)).ToHtmlString());
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => new(default(TValue)!);
    }
}
