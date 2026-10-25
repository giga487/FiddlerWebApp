using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Fiddler.Formats;
using Fiddler.Formats.Hues;
using Fiddler.Plugin.AssetInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Serilog.Parsing;
using FiddlerWebApp.Services.Diagnostics;
using UOAS.Application.Collaboration;
using UOAS.Contracts;
using UOAS.Presentation.Services;
using UOAS.Presentation.ViewModels;
using Xunit;

namespace UOAS.Tests;

public sealed class WorkshopTests
{
    private static async Task<string> Workspace(HttpClient client)
    {
        using var opened = await client.PostAsJsonAsync("/api/workspaces", new OpenWorkspaceRequest("default")); opened.EnsureSuccessStatusCode();
        return $"/api/workspaces/{(await opened.Content.ReadFromJsonAsync<WorkspaceState>())!.Id}";
    }
    [Fact]
    public async Task ComparisonCopiesOnlyToOwnedIdAndKeepsItsReferenceSnapshot()
    {
        await using var host = new LockIntegrationTests.Host();
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        string a = await Workspace(alice), b = await Workspace(bob);
        using var compared = await alice.PostAsJsonAsync(a + "/comparison", new CompareRequest("alias")); compared.EnsureSuccessStatusCode();
        Assert.Empty((await compared.Content.ReadFromJsonAsync<ComparisonPage>())!.Items);
        var all = await alice.GetFromJsonAsync<ComparisonPage>(a + "/comparison?differencesOnly=false");
        Assert.Contains(all!.Items, row => row.Id == 10 && row.Difference == "Equal");
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync(a + "/comparison/copy", new CopyGumpRequest(10, 11))).StatusCode);
        (await bob.PostAsync(b + "/locks/Gump/11", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(a + "/locks/Gump/11", null)).StatusCode);
        (await bob.DeleteAsync(b + "/locks/Gump/11")).EnsureSuccessStatusCode();
        (await alice.PostAsync(a + "/locks/Gump/11", null)).EnsureSuccessStatusCode();
        (await alice.PostAsJsonAsync(a + "/comparison/copy", new CopyGumpRequest(10, 11))).EnsureSuccessStatusCode();
        Assert.True((await alice.GetFromJsonAsync<AssetDetails>(a + "/assets/Gump/11"))!.Draft);
        (await alice.PostAsync(a + "/publish/Gump", null)).EnsureSuccessStatusCode();
        (await bob.PostAsync(b + "/locks/Gump/10", null)).EnsureSuccessStatusCode();
        var png = new ByteArrayContent(new Fiddler.Graphics.SkiaAssetRenderer().RenderPng(DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]))); png.Headers.ContentType = new("image/png");
        (await bob.PutAsync(b + "/assets/Gump/10/image", png)).EnsureSuccessStatusCode();
        (await bob.PostAsync(b + "/publish/Gump", null)).EnsureSuccessStatusCode();
        (await alice.PostAsync(a + "/refresh", null)).EnsureSuccessStatusCode();
        // A subsequent published reference change does not replace this comparison's captured reader.
        Assert.Equal(await alice.GetByteArrayAsync(a + "/assets/Gump/11/image"), await alice.GetByteArrayAsync(a + "/comparison/reference/10/image"));
        Assert.NotEqual(await alice.GetByteArrayAsync(a + "/assets/Gump/10/image"), await alice.GetByteArrayAsync(a + "/comparison/reference/10/image"));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync(a + "/comparison", new CompareRequest("../private"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(a + "/comparison")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(a + "/tools/map?facet=0&x=0&y=0")).StatusCode);
    }

    [Fact]
    public async Task ComparisonViewModelCanChangeDestinationAcquireAndCopyWithoutLosingSelection()
    {
        await using var host = new LockIntegrationTests.Host(); using var http = host.CreateClient(); var api = new AssetApiClient(http);
        await using var workspace = new WorkspaceViewModel(api) { DisplayName = "Designer" };
        await workspace.StartSessionCommand.ExecuteAsync(); await workspace.OpenCommand.ExecuteAsync();
        using var compare = new ComparisonViewModel(api, workspace) { Library = "default", DifferencesOnly = false };
        await compare.CompareCommand.ExecuteAsync(); Assert.Null(compare.Error);
        await compare.SelectAsync(Assert.Single(compare.Result!.Items)); Assert.Null(compare.Error);
        compare.TargetId = 11; await compare.TargetChangedAsync(); Assert.False(compare.Busy); Assert.Null(compare.Error); Assert.NotNull(compare.Selected);
        Assert.True(compare.AcquireCommand.CanExecute(null));
        await compare.AcquireCommand.ExecuteAsync(); Assert.Null(compare.Error); Assert.True(compare.CopyCommand.CanExecute(null));
        await compare.CopyCommand.ExecuteAsync(); Assert.Null(compare.Error); Assert.Equal(1, workspace.Workspace!.GumpDraftCount);
    }

    [Fact]
    public void BrowserPaletteMatchesNativeRgb555GradientAndPartialHueRules()
    {
        var colors = PaletteMath.Gradient(PaletteMath.Parse("#101020"), PaletteMath.Parse("#20AA50"), PaletteMath.Parse("#FFF0EE"));
        Assert.Equal(HuePalette.Gradient(HuePalette.FromRgb(16,16,32), HuePalette.FromRgb(32,170,80), HuePalette.FromRgb(255,240,238)), colors);
        uint[] pixels = [0, 0xFF808080, 0x88F81820, 0x01080808, 0xFFFFFFFF, 0xFF000000, 0xFF999899];
        foreach (bool partial in new[] { true, false })
        {
            var native = HuePalette.Apply(DecodedImage.FromArgb(pixels.Length, 1, pixels.Select(pixel => unchecked((int)pixel)).ToArray()), colors, partial ? HueMode.GrayPixelsOnly : HueMode.Full);
            Assert.Equal(native.Pixels.ToArray(), pixels.Select(pixel => unchecked((int)PaletteMath.Apply(pixel, colors, partial))).ToArray());
        }
    }

    [Fact]
    public async Task AnimationCatalogExposesOnlyPresentBodiesActionsAndMirroredDirections()
    {
        await using var host = new LockIntegrationTests.Host();
        byte[] record = new byte[537];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 0x7C00);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(512), 1);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(516), 8);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(520), -1);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(522), -2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(524), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(526), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(528), (1U << 22) | 1);
        record[532] = 1; BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(533), 0x7FFF7FFF);
        File.WriteAllBytes(Path.Combine(host.Data, "anim.mul"), record);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(host.Data, "anim.idx"))))
        { writer.Write(-1); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(record.Length); writer.Write(0); }
        using var client = await host.Client("Designer"); string root = await Workspace(client);
        Assert.Single((await client.GetFromJsonAsync<AnimationArchive[]>(root + "/tools/animations"))!);
        var page = await client.GetFromJsonAsync<AnimationBodyPage>(root + "/tools/animations/1/bodies?category=Monster");
        Assert.Equal(1, page!.Total); Assert.Equal(0, Assert.Single(page.Items).Id);
        var actions = await client.GetFromJsonAsync<AnimationActionInfo[]>(root + "/tools/animations/1/bodies/0/actions");
        Assert.Equal(new[] { 1, 7 }, Assert.Single(actions!).Directions);
        Assert.Empty((await client.GetFromJsonAsync<AnimationBodyPage>(root + "/tools/animations/1/bodies?category=Animal"))!.Items);
        var atlas = await client.GetFromJsonAsync<AnimationAtlas>(root + "/tools/animations/1/0/0/7");
        Assert.Equal(1, atlas!.FrameCount); Assert.NotEmpty(Convert.FromBase64String(atlas.Image));
    }

    [Fact]
    public async Task DedicatedMetadataAndHueEditorsRoundTripThroughTheBackend()
    {
        await using var host = new LockIntegrationTests.Host();
        File.WriteAllBytes(Path.Combine(host.Data, "tiledata.mul"), new byte[512 * (4 + 32 * 26) + 4 + 32 * 37]);
        File.WriteAllBytes(Path.Combine(host.Data, "hues.mul"), new byte[708]);
        using var http = host.CreateClient(); var api = new AssetApiClient(http);
        await using var workspace = new WorkspaceViewModel(api) { DisplayName = "Designer" };
        await workspace.StartSessionCommand.ExecuteAsync(); await workspace.OpenCommand.ExecuteAsync();
        await using var document = new DocumentViewModel(api, workspace);
        await document.FilesCommand.ExecuteAsync();
        document.FileIndex = document.AvailableFiles.ToList().FindIndex(file => file.Kind == DocumentKind.TileDataItem);
        await document.OpenCommand.ExecuteAsync(); await document.SelectAsync(10); await document.AcquireCommand.ExecuteAsync();
        await workspace.RefreshLocksAsync(); Assert.NotNull(document.Document); Assert.NotNull(document.Selected);
        document.Name = "Edited item"; document.ToggleFlag(0x40000, true); document.SetNumericField("height", "42"); document.SetNumericField("animation", "65535");
        await document.SaveCommand.ExecuteAsync(); Assert.Null(document.Error);
        Assert.Equal(42, document.Selected!.Value.GetProperty("height").GetInt32()); Assert.Equal(65535, document.NumericField("animation")); Assert.True(document.HasFlag(0x40000));
        var mode = await api.GetAsync<HueModeInfo>(workspace.WorkspacePath + "/tools/hue-mode/Item/10"); Assert.True(mode.PartialHue);
        await document.PublishCommand.ExecuteAsync(); Assert.Null(document.Error);
        document.FileIndex = document.AvailableFiles.ToList().FindIndex(file => file.Kind == DocumentKind.Hue);
        await document.OpenCommand.ExecuteAsync(); await document.SelectAsync(1); await document.AcquireCommand.ExecuteAsync();
        var original = document.Palette.ToArray(); document.GenerateHue(); Assert.NotEqual(original, document.Palette);
        document.UndoHue(); Assert.Equal(original, document.Palette);
        document.GenerateHue(); document.Name = "Generated"; var generated = document.Palette.ToArray();
        await document.SaveCommand.ExecuteAsync(); Assert.Null(document.Error); Assert.Equal(generated, document.Palette);
        await document.PublishCommand.ExecuteAsync(); Assert.Null(document.Error); Assert.Equal("Generated", document.Name);
    }

    [Fact]
    public async Task DiagnosticsFiltersTimesExceptionsAndSequenceWithoutDeletingOtherSessions()
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer");
        var logs = host.Services.GetRequiredService<DiagnosticLogStore>();
        var time = DateTimeOffset.UtcNow;
        void Emit(DateTimeOffset timestamp, Exception? exception) => logs.Emit(new LogEvent(timestamp, LogEventLevel.Error, exception, new MessageTemplateParser().Parse("Test diagnostic"), [new LogEventProperty("SourceContext", new ScalarValue("WorkshopTests"))]));
        Emit(time.AddHours(-1), null); Emit(time, new InvalidOperationException("Test exception"));
        var matched = await client.GetFromJsonAsync<DiagnosticEvent[]>("/api/diagnostics?category=WorkshopTests&exceptionsOnly=true&from=" + Uri.EscapeDataString(time.AddMinutes(-1).ToString("O")));
        Assert.Single(matched!); Assert.Contains("Test exception", matched![0].Exception);
        Assert.Empty((await client.GetFromJsonAsync<DiagnosticEvent[]>($"/api/diagnostics?category=WorkshopTests&afterSequence={logs.LatestSequence}"))!);
        Assert.Equal(2, (await client.GetFromJsonAsync<DiagnosticEvent[]>("/api/diagnostics?category=WorkshopTests"))!.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/diagnostics?from=2030-01-01&until=2020-01-01")).StatusCode);
    }

    [Fact]
    public async Task PluginPackagesRemainInertUntilExactHashApprovedAndUseDetachedImages()
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer"); string workspace = await Workspace(client);
        byte[] dll = File.ReadAllBytes(typeof(AssetInfoPlugin).Assembly.Location);
        var request = new InstallPluginRequest("AssetInfo.dll", "", Convert.ToBase64String(dll));
        using var uploaded = await client.PostAsJsonAsync("/api/plugins", request); uploaded.EnsureSuccessStatusCode();
        var package = (await uploaded.Content.ReadFromJsonAsync<PluginPackage>())!;
        Assert.False(package.Trusted); Assert.False(package.Active); Assert.Empty(package.Commands);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/plugins/{package.Key}/activate", null)).StatusCode);
        var configuration = host.Services.GetRequiredService<IConfiguration>(); configuration["Plugins:TrustedPackageHashes:0"] = Convert.ToHexString(SHA256.HashData(dll));
        using var activated = await client.PostAsync($"/api/plugins/{package.Key}/activate", null); activated.EnsureSuccessStatusCode();
        var active = (await activated.Content.ReadFromJsonAsync<PluginPackage>())!; Assert.True(active.Active); Assert.Single(active.Commands);
        Guid workspaceId = Guid.Parse(workspace.Split('/').Last());
        var before = await client.GetByteArrayAsync(workspace + "/assets/Item/10/image");
        using var executed = await client.PostAsJsonAsync($"/api/plugins/{package.Key}/execute", new ExecutePluginRequest(workspaceId, ResourceKind.Item, 10, "inspect")); executed.EnsureSuccessStatusCode();
        Assert.Contains("1 × 1", (await executed.Content.ReadFromJsonAsync<PluginOutput>())!.Text);
        Assert.Equal(before, await client.GetByteArrayAsync(workspace + "/assets/Item/10/image"));
        configuration["Plugins:TrustedPackageHashes:0"] = "revoked";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/plugins/{package.Key}/execute", new ExecutePluginRequest(workspaceId, ResourceKind.Item, 10, "inspect"))).StatusCode);
        (await client.PostAsync($"/api/plugins/{package.Key}/deactivate", null)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:\\escape.dll")]
    [InlineData("folder/../../escape.dll")]
    public async Task PluginQuarantineRejectsZipTraversalBeforeExtraction(string entry)
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true)) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write("inert"); }
        using var uploaded = await client.PostAsJsonAsync("/api/plugins", new InstallPluginRequest("test.zip", "Plugin.dll", Convert.ToBase64String(bytes.ToArray())));
        Assert.Equal(HttpStatusCode.BadRequest, uploaded.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PluginPackage[]>("/api/plugins"))!);
    }
}
