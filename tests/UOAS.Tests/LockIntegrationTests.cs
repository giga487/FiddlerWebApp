using System.Net;
using System.Net.Http.Json;
using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;
using Fiddler.Graphics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UOAS.Backend;
using UOAS.Contracts;
using Xunit;

namespace UOAS.Tests;

public sealed class LockIntegrationTests
{
    private const int Red = unchecked((int)0xFFFF0000), Blue = unchecked((int)0xFF0000FF);
    private static byte[] Png(int color) => new SkiaAssetRenderer().RenderPng(DecodedImage.FromArgb(1, 1, [color]));
    private static async Task<WorkspaceState> Open(HttpClient client, string library = "default")
    {
        using var response = await client.PostAsJsonAsync("/api/workspaces", new OpenWorkspaceRequest(library));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<WorkspaceState>())!;
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient client, string path, int color)
    { var content = new ByteArrayContent(Png(color)); content.Headers.ContentType = new("image/png"); return await client.PutAsync(path + "/image", content); }

    [Fact]
    public async Task SameIdIsLockedByNameAcrossSessionsWhileDifferentIdsRemainEditable()
    {
        await using var host = new Host();
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        var a = await Open(alice); var b = await Open(bob, "alias");
        string ar = $"/api/workspaces/{a.Id}", br = $"/api/workspaces/{b.Id}";
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync(ar + "/locks/Item/10", null)).StatusCode);
        using var blocked = await bob.PostAsync(br + "/locks/Item/10", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Alice", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(bob, br + "/assets/Item/10", Blue)).StatusCode);
        var owners = await bob.GetFromJsonAsync<ResourceLock[]>(br + "/locks");
        Assert.Equal("Alice", Assert.Single(owners!).UserName); Assert.False(owners![0].Own);
        Assert.True(Assert.Single((await alice.GetFromJsonAsync<ResourceLock[]>(ar + "/locks"))!).Own);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync(br + "/locks/Item/11", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync(ar + "/locks/Land/10", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync(br + "/locks/Gump/10", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Upload(alice, ar + "/assets/Item/10", Red)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.DeleteAsync(ar + "/locks/Item/10")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync(ar + "/locks/Item/10?discard=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync(br + "/locks/Item/10", null)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentDifferentIdPublishingMergesChangesAndNeverMutatesOriginalFiles()
    {
        await using var host = new Host(); byte[] original = File.ReadAllBytes(Path.Combine(host.Data, "art.mul"));
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        var a = await Open(alice); var b = await Open(bob);
        string ar = $"/api/workspaces/{a.Id}", br = $"/api/workspaces/{b.Id}";
        (await alice.PostAsync(ar + "/locks/Item/10", null)).EnsureSuccessStatusCode();
        (await bob.PostAsync(br + "/locks/Item/11", null)).EnsureSuccessStatusCode();
        (await Upload(alice, ar + "/assets/Item/10", Red)).EnsureSuccessStatusCode();
        (await Upload(bob, br + "/assets/Item/11", Blue)).EnsureSuccessStatusCode();
        (await bob.PostAsync(br + "/publish/Item", null)).EnsureSuccessStatusCode();
        using var published = await alice.PostAsync(ar + "/publish/Item", null); published.EnsureSuccessStatusCode();
        var result = (await published.Content.ReadFromJsonAsync<ExportResult>())!;
        using var art = ArtDocument.Open(Path.Combine(result.Directory, "artidx.mul"), Path.Combine(result.Directory, "art.mul"));
        Assert.Equal(Red, art.Read(AssetKind.Item, 10).Pixels.Span[0]);
        Assert.Equal(Blue, art.Read(AssetKind.Item, 11).Pixels.Span[0]);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(host.Data, "art.mul")));
        (await bob.PostAsync(br + "/refresh", null)).EnsureSuccessStatusCode();
        Assert.Empty((await alice.GetFromJsonAsync<ResourceLock[]>(ar + "/locks"))!);
    }

    [Fact]
    public async Task ExpiredOwnerCannotWritePublishOrReleaseAnotherOwnersLease()
    {
        await using var host = new Host();
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        var a = await Open(alice); var b = await Open(bob);
        string ar = $"/api/workspaces/{a.Id}", br = $"/api/workspaces/{b.Id}";
        (await alice.PostAsync(ar + "/locks/Item/10", null)).EnsureSuccessStatusCode();
        (await Upload(alice, ar + "/assets/Item/10", Red)).EnsureSuccessStatusCode();
        host.Clock.Advance(TimeSpan.FromMinutes(3));
        (await bob.PostAsync(br + "/locks/Item/10", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(alice, ar + "/assets/Item/10", Red)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(ar + "/publish/Item", null)).StatusCode);
        (await alice.DeleteAsync(ar + "/locks/Item/10?discard=true")).EnsureSuccessStatusCode();
        Assert.Equal("Bob", Assert.Single((await alice.GetFromJsonAsync<ResourceLock[]>(ar + "/locks"))!).UserName);
        (await bob.DeleteAsync("/api/session")).EnsureSuccessStatusCode();
        Assert.Empty((await alice.GetFromJsonAsync<ResourceLock[]>(ar + "/locks"))!);
    }

    [Fact]
    public async Task BackendRestrictsLibrariesWorkspaceOwnershipAndPageSize()
    {
        await using var host = new Host();
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        var a = await Open(alice);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/workspaces/{a.Id}/locks")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/workspaces", new OpenWorkspaceRequest("../private"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync("/api/workspaces", new OpenWorkspaceRequest("missing"))).StatusCode);
        var page = await alice.GetFromJsonAsync<AssetPage>($"/api/workspaces/{a.Id}/assets/Item?limit=12");
        Assert.Equal(12, page!.Items.Count); Assert.Contains(page.Items, slot => slot.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/workspaces/{a.Id}/assets/Item?limit=100000")).StatusCode);
        using var anon = host.CreateClient(); Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/api/libraries")).StatusCode);
    }

    [Fact]
    public async Task AnExpiredUnusedClaimCanReacquireAndRefreshAfterAnotherDesignerPublishes()
    {
        await using var host = new Host();
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        var a = await Open(alice); var b = await Open(bob);
        string ar = $"/api/workspaces/{a.Id}", br = $"/api/workspaces/{b.Id}";
        (await alice.PostAsync(ar + "/locks/Item/10", null)).EnsureSuccessStatusCode();
        host.Clock.Advance(TimeSpan.FromMinutes(3));
        (await bob.PostAsync(br + "/locks/Item/10", null)).EnsureSuccessStatusCode();
        (await Upload(bob, br + "/assets/Item/10", Red)).EnsureSuccessStatusCode();
        (await bob.PostAsync(br + "/publish/Item", null)).EnsureSuccessStatusCode();
        (await alice.PostAsync(ar + "/locks/Item/10", null)).EnsureSuccessStatusCode();
        Assert.True((await alice.GetFromJsonAsync<AssetDetails>(ar + "/assets/Item/10"))!.Lock!.Own);
        byte[] image = await alice.GetByteArrayAsync(ar + "/assets/Item/10/image");
        using var bitmap = SkiaSharp.SKBitmap.Decode(image);
        Assert.Equal(SkiaSharp.SKColors.Red, bitmap.GetPixel(0, 0));
    }

    public sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    public sealed class Host : WebApplicationFactory<BackendProgram>
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "uoas-backend-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(root, "client");
        public string RegionFile => Path.Combine(root, "regions.json");
        public TestClock Clock { get; } = new();
        public Host()
        {
            Directory.CreateDirectory(Data);
            File.WriteAllText(RegionFile, """
                [
                  { "$type":"BaseRegion", "Name":"Harbor", "Map":"Felucca", "Priority":50, "Area":[{"x1":0,"y1":0,"x2":100,"y2":100}], "CustomRule":{"keep":true} },
                  { "$type":"VisibilityRegion", "Name":"Cellar", "Map":"Felucca", "Priority":100, "Parent":{"Name":"Harbor","Map":"Felucca"}, "Area":[{"x1":10,"y1":10,"z1":-20,"x2":20,"y2":20,"z2":0}], "Visibility3DMode":"Enabled", "Visibility3DRadius":24 },
                  { "$type":"TownRegion", "Name":"Harbor", "Map":"Sea", "Priority":50, "Area":[{"x1":100,"y1":100,"x2":200,"y2":200}] }
                ]
                """);
            byte[] image = ArtEncoder.Item(DecodedImage.FromArgb(1, 1, [Blue]));
            File.WriteAllBytes(Path.Combine(Data, "art.mul"), image);
            using (var index = new BinaryWriter(File.Create(Path.Combine(Data, "artidx.mul"))))
                for (int slot = 0; slot < 0x400d; slot++) { index.Write(slot == 0x400a ? 0 : -1); index.Write(slot == 0x400a ? image.Length : 0); index.Write(0); }
            var gump = GumpEncoder.Encode(DecodedImage.FromArgb(1, 1, [Blue])); File.WriteAllBytes(Path.Combine(Data, "gumpart.mul"), gump.Data);
            using var gumpIndex = new BinaryWriter(File.Create(Path.Combine(Data, "gumpidx.mul")));
            for (int slot = 0; slot < 12; slot++) { gumpIndex.Write(slot == 10 ? 0 : -1); gumpIndex.Write(slot == 10 ? gump.Data.Length : 0); gumpIndex.Write(slot == 10 ? 0x10001 : 0); }
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(root);
            builder.UseSetting("Storage:RootDirectory", root);
            builder.UseSetting("Assets:Libraries:default:Directory", Data);
            builder.UseSetting("Assets:Libraries:alias:Directory", Path.Combine(Data, "."));
            builder.UseSetting("Assets:ExportDirectory", Path.Combine(root, "exports"));
            builder.UseSetting("Regions:Sources:default:File", RegionFile);
            builder.UseSetting("Regions:Sources:alias:File", Path.Combine(root, ".", "regions.json"));
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); });
        }
        public async Task<HttpClient> Client(string name)
        {
            var client = CreateClient();
            using var response = await client.PostAsJsonAsync("/api/sessions", new StartSessionRequest(name)); response.EnsureSuccessStatusCode();
            var session = (await response.Content.ReadFromJsonAsync<CollaborationSession>())!;
            client.DefaultRequestHeaders.Add("X-UOAS-Session", session.Token); return client;
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            for (int attempt = 0; Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) when (attempt < 10) { await Task.Delay(100); }
            }
        }
    }
}
