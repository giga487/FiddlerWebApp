using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using SkiaSharp;
using UOAS.Contracts;
using UOAS.Presentation.Services;
using UOAS.Presentation.ViewModels;
using Xunit;

namespace UOAS.Tests;

public sealed class AtlasTests
{
    internal static void Fixture(string directory, int facet = 0, int width = 512, int height = 256)
    {
        using (var map = new BinaryWriter(File.Create(Path.Combine(directory, $"map{facet}.mul"))))
        {
            for (int bx = 0; bx < width / 8; bx++) for (int by = 0; by < height / 8; by++)
            { map.Write(0); for (int i = 0; i < 64; i++) { map.Write((ushort)(bx % 2 == 0 ? 1 : 2)); map.Write((byte)0); } }
        }
        using var colors = new BinaryWriter(File.Create(Path.Combine(directory, "radarcol.mul"))); colors.Write((ushort)0); colors.Write((ushort)0x7c00); colors.Write((ushort)0x001f);
    }
    private static async Task<AtlasMap> Setup(HttpClient client, string name = "Felucca", int facet = 0)
    {
        var settings = (await client.GetFromJsonAsync<WorkspaceSettings>("/api/configuration"))!;
        (await client.PutAsJsonAsync("/api/configuration", settings with { Libraries = settings.Libraries.Select(library => library with { Maps = [new(facet, name, 512, 256)] }).ToArray() })).EnsureSuccessStatusCode();
        return (await client.GetFromJsonAsync<AtlasCatalog>("/api/atlas/maps"))!.Maps.First(map => map.Library == "default" && map.FileId == facet);
    }
    private static string Tile(AtlasMap map, int scale = 1, int x = 0, int y = 0) => $"/api/atlas/{map.Library}/{map.FileId}/tiles/{scale}/{x}/{y}?revision={map.Revision}";
    [Fact]
    public async Task RadarTilesPreserveCoordinatesAndAggregateColorsAtLowerDetail()
    {
        await using var host = new LockIntegrationTests.Host(); Fixture(host.Data); byte[] original = File.ReadAllBytes(Path.Combine(host.Data, "map0.mul"));
        using var client = await host.Client("Designer"); var map = await Setup(client); Assert.True(map.Ready);
        using var detail = SKBitmap.Decode(await client.GetByteArrayAsync(Tile(map))); Assert.Equal(256, detail.Width);
        Assert.Equal(SKColors.Red, detail.GetPixel(0, 0)); Assert.Equal(SKColors.Blue, detail.GetPixel(8, 0));
        using var overview = SKBitmap.Decode(await client.GetByteArrayAsync(Tile(map, 8))); Assert.Equal(SKColors.Red, overview.GetPixel(0, 0)); Assert.Equal(SKColors.Blue, overview.GetPixel(1, 0)); Assert.Equal((byte)0, overview.GetPixel(64, 0).Alpha);
        using var reduced = SKBitmap.Decode(await client.GetByteArrayAsync(Tile(map, 16))); Assert.Equal(new SKColor(128, 0, 128), reduced.GetPixel(0, 0));
        byte[] first = await client.GetByteArrayAsync(Tile(map, 16)); Assert.Equal(first, await client.GetByteArrayAsync(Tile(map, 16)));
        using var thumb = SKBitmap.Decode(await client.GetByteArrayAsync($"/api/atlas/default/0/thumbnail?revision={map.Revision}")); Assert.Equal((64, 32), (thumb.Width, thumb.Height));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(host.Data, "map0.mul")));
    }
    [Fact]
    public async Task CompressedUopMapsProduceTheSameOverviewAsMulAndReuseDiskCache()
    {
        await using var host = new LockIntegrationTests.Host(); Fixture(host.Data); using var client = await host.Client("Designer");
        var mul = await Setup(client); byte[] expected = await client.GetByteArrayAsync(Tile(mul, 8));
        byte[] payload = File.ReadAllBytes(Path.Combine(host.Data, "map0.mul"));
        using var compressed = new MemoryStream(); using (var stream = new ZLibStream(compressed, CompressionLevel.Fastest, true)) stream.Write(payload);
        byte[] packed = compressed.ToArray();
        using (var writer = new BinaryWriter(File.Create(Path.Combine(host.Data, "map0legacymul.uop"))))
        {
            writer.Write(0x50594DU); writer.Write(5U); writer.Write(0U); writer.Write(512L); writer.Write(1); writer.Write(1);
            writer.BaseStream.Position = 512; writer.Write(1); writer.Write(0L); writer.Write(558L); writer.Write(0); writer.Write(packed.Length); writer.Write(payload.Length);
            writer.Write(Fiddler.Formats.Containers.Uop.UopNameHash.HashFileName("build/map0legacymul/00000000.dat")); writer.Write(0); writer.Write((short)1); writer.Write(packed);
        }
        var uop = (await client.GetFromJsonAsync<AtlasCatalog>("/api/atlas/maps"))!.Maps.First(map => map.Library == "default");
        Assert.NotEqual(mul.Revision, uop.Revision); Assert.Equal(expected, await client.GetByteArrayAsync(Tile(uop, 8)));
        string cache = Path.Combine(Path.GetDirectoryName(host.Data)!, "App_Data", "atlas-cache"); var stamps = Directory.GetFiles(cache, "*.png").ToDictionary(file => file, File.GetLastWriteTimeUtc);
        _ = await client.GetByteArrayAsync(Tile(uop, 8)); Assert.All(stamps, stamp => Assert.Equal(stamp.Value, File.GetLastWriteTimeUtc(stamp.Key)));
    }
    [Fact]
    public async Task CustomMapsRequireDimensionsAndPaletteChangesInvalidateTheCache()
    {
        await using var host = new LockIntegrationTests.Host(); Fixture(host.Data, 25); using var client = await host.Client("Designer");
        var missing = (await client.GetFromJsonAsync<AtlasCatalog>("/api/atlas/maps"))!.Maps.First(); Assert.False(missing.Ready); Assert.Contains("dimensions", missing.Detail, StringComparison.OrdinalIgnoreCase);
        var map = await Setup(client, "Sea", 25); Assert.Equal("Sea", map.Name); Assert.True(map.Ready);
        (await client.GetAsync(Tile(map, 8))).EnsureSuccessStatusCode();
        byte[] colors = File.ReadAllBytes(Path.Combine(host.Data, "radarcol.mul")); colors[2] = 0xe0; colors[3] = 3; File.WriteAllBytes(Path.Combine(host.Data, "radarcol.mul"), colors); File.SetLastWriteTimeUtc(Path.Combine(host.Data, "radarcol.mul"), DateTime.UtcNow.AddSeconds(2));
        var updated = (await client.GetFromJsonAsync<AtlasCatalog>("/api/atlas/maps"))!.Maps.First(m => m.Library == "default"); Assert.NotEqual(map.Revision, updated.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(Tile(map, 8))).StatusCode);
        using var tile = SKBitmap.Decode(await client.GetByteArrayAsync(Tile(updated, 8))); Assert.Equal(SKColors.Lime, tile.GetPixel(0, 0));
    }
    [Fact]
    public async Task AtlasRejectsInvalidTilesAndWorksAcrossIndependentSessions()
    {
        await using var host = new LockIntegrationTests.Host(); Fixture(host.Data); using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob"); var map = await Setup(alice);
        var images = await Task.WhenAll(alice.GetByteArrayAsync(Tile(map, 8)), bob.GetByteArrayAsync(Tile(map, 8))); Assert.Equal(images[0], images[1]);
        foreach (string path in new[] { Tile(map, 3), Tile(map, 1, 999), Tile(map, 1, -1) }) Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync(path)).StatusCode);
        using var anonymous = host.CreateClient(); Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync(Tile(map))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/atlas/default/256/tiles/1/0/0?revision=x")).StatusCode);
    }
    [Fact]
    public async Task RegionOverlayFiltersViewportHeightAndShowsNamedLocksWithoutAcquiringThem()
    {
        await using var host = new LockIntegrationTests.Host(); using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        async Task<string> Open(HttpClient client) { using var result = await client.PostAsJsonAsync("/api/regions", new OpenRegionsRequest("default")); result.EnsureSuccessStatusCode(); return "/api/regions/" + (await result.Content.ReadFromJsonAsync<RegionCatalog>())!.Id; }
        string a = await Open(alice), b = await Open(bob); (await alice.PostAsync(a + "/locks/1", null)).EnsureSuccessStatusCode();
        var visible = (await bob.GetFromJsonAsync<RegionOverlayPage>(b + "/overlay?map=Felucca&x=0&y=0&width=100&height=100&z=-10"))!; Assert.Equal(2, visible.Total);
        var cellar = visible.Items.Single(item => item.Summary.Id == 1); Assert.Equal("Alice", cellar.Summary.Lock!.UserName); Assert.False(cellar.Summary.Lock.Own);
        Assert.Single((await bob.GetFromJsonAsync<RegionOverlayPage>(b + "/overlay?map=Felucca&x=0&y=0&width=100&height=100&z=0"))!.Items);
        Assert.Empty((await bob.GetFromJsonAsync<RegionOverlayPage>(b + "/overlay?map=Felucca&x=200&y=200&width=50&height=50"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(a + "/overlay?map=Felucca&x=0&y=0&width=100&height=100")).StatusCode);
        var record = (await bob.GetFromJsonAsync<RegionRecord>(b + "/records/1"))!;
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutAsJsonAsync(b + "/records/1", record.Value)).StatusCode);
    }
    [Fact]
    public async Task CanvasGeometryRequiresOwnershipAndPreservesTheVolumesVerticalRange()
    {
        await using var host = new LockIntegrationTests.Host(); Fixture(host.Data); using var http = host.CreateClient(); var api = new AssetApiClient(http);
        await using var workspace = new WorkspaceViewModel(api) { DisplayName = "Alice" }; await workspace.StartSessionCommand.ExecuteAsync(); http.DefaultRequestHeaders.Add("X-UOAS-Session", api.Session!.Token); await Setup(http);
        await using var regions = new RegionsViewModel(api, workspace); await regions.Atlas.LoadMapsAsync(); await regions.SourcesCommand.ExecuteAsync(); regions.Source = "default"; await regions.OpenCommand.ExecuteAsync(); await regions.SelectAsync(1);
        var original = regions.Area[0]; regions.ApplyAtlasVolume(new(0, original with { X2 = 30 })); Assert.Equal(original, regions.Area[0]);
        await regions.AcquireCommand.ExecuteAsync(); regions.ApplyAtlasVolume(new(0, original with { X2 = 30, Z1 = 99, Z2 = 100 })); Assert.Equal(30, regions.Area[0].X2); Assert.Equal(-20, regions.Area[0].Z1); Assert.Equal(0, regions.Area[0].Z2);
        await regions.SaveCommand.ExecuteAsync(); Assert.Null(regions.Error); await regions.Atlas.SettleAsync(); Assert.Contains(regions.AtlasOverlays, overlay => overlay.Summary.Id == 1 && overlay.Area[0].X2 == 30);
        await regions.SelectFromAtlasAsync(1); Assert.Equal(1, regions.FocusedId); Assert.Single(regions.Rows);
    }
}
