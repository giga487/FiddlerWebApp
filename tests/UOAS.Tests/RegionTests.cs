using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UOAS.Application.Regions;
using UOAS.Contracts;
using UOAS.Presentation.Services;
using UOAS.Presentation.ViewModels;
using Xunit;

namespace UOAS.Tests;

public sealed class RegionTests
{
    private static async Task<string> Open(HttpClient client, string source = "default")
    {
        using var opened = await client.PostAsJsonAsync("/api/regions", new OpenRegionsRequest(source)); opened.EnsureSuccessStatusCode();
        return "/api/regions/" + (await opened.Content.ReadFromJsonAsync<RegionCatalog>())!.Id;
    }
    private static JsonObject Edit(RegionRecord record) => JsonNode.Parse(record.Value.GetRawText())!.AsObject();
    [Fact]
    public async Task RegionLocksUseStableIdsAcrossSourceAliasesAndMergeDifferentRecords()
    {
        await using var host = new LockIntegrationTests.Host(); byte[] original = File.ReadAllBytes(host.RegionFile);
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        string a = await Open(alice), b = await Open(bob, "alias");
        (await alice.PostAsync(a + "/locks/0", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync(b + "/locks/0", null)).StatusCode);
        var viewed = (await bob.GetFromJsonAsync<RegionRecord>(b + "/records/0"))!;
        Assert.Equal("Alice", viewed.Summary.Lock!.UserName); Assert.False(viewed.Summary.Lock.Own);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutAsJsonAsync(b + "/records/0", viewed.Value)).StatusCode);
        (await bob.PostAsync(b + "/locks/2", null)).EnsureSuccessStatusCode();
        var av = Edit((await alice.GetFromJsonAsync<RegionRecord>(a + "/records/0"))!); av["Priority"] = 77;
        var bv = Edit((await bob.GetFromJsonAsync<RegionRecord>(b + "/records/2"))!); bv["Priority"] = 88;
        (await alice.PutAsJsonAsync(a + "/records/0", av)).EnsureSuccessStatusCode();
        (await bob.PutAsJsonAsync(b + "/records/2", bv)).EnsureSuccessStatusCode();
        (await bob.PostAsync(b + "/publish", null)).EnsureSuccessStatusCode();
        using var published = await alice.PostAsync(a + "/publish", null); published.EnsureSuccessStatusCode();
        var export = (await published.Content.ReadFromJsonAsync<RegionExport>())!;
        var rows = RegionDocument.Parse(Encoding.UTF8.GetBytes(export.Json)); Assert.Equal(3, rows.Length);
        Assert.Equal(77, rows.Single(row => RegionDocument.Identity(row) == "Felucca\0Harbor").GetProperty("Priority").GetInt32());
        Assert.Equal(88, rows.Single(row => RegionDocument.Identity(row) == "Sea\0Harbor").GetProperty("Priority").GetInt32());
        Assert.True(rows[0].GetProperty("CustomRule").GetProperty("keep").GetBoolean());
        Assert.Equal(original, File.ReadAllBytes(host.RegionFile));
        (await bob.PostAsync(b + "/refresh", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await bob.GetFromJsonAsync<RegionRecord>(b + "/records/0"))!.Summary.Id);
        Assert.Empty((await bob.GetFromJsonAsync<DocumentLock[]>(b + "/locks"))!);
    }
    [Fact]
    public async Task ParentRenameAndDeletionRequireAValidCombinedHierarchy()
    {
        await using var host = new LockIntegrationTests.Host(); using var alice = await host.Client("Alice"); string root = await Open(alice);
        (await alice.PostAsync(root + "/locks/0", null)).EnsureSuccessStatusCode();
        var parent = Edit((await alice.GetFromJsonAsync<RegionRecord>(root + "/records/0"))!); parent["Name"] = "Renamed harbor";
        (await alice.PutAsJsonAsync(root + "/records/0", parent)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(root + "/publish", null)).StatusCode);
        (await alice.PostAsync(root + "/locks/1", null)).EnsureSuccessStatusCode();
        var child = Edit((await alice.GetFromJsonAsync<RegionRecord>(root + "/records/1"))!); child["Parent"]!["Name"] = "Renamed harbor";
        (await alice.PutAsJsonAsync(root + "/records/1", child)).EnsureSuccessStatusCode();
        (await alice.PostAsync(root + "/publish", null)).EnsureSuccessStatusCode();
        (await alice.PostAsync(root + "/locks/0", null)).EnsureSuccessStatusCode();
        (await alice.DeleteAsync(root + "/records/0")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(root + "/publish", null)).StatusCode);
        (await alice.PostAsync(root + "/locks/1", null)).EnsureSuccessStatusCode();
        (await alice.DeleteAsync(root + "/records/1")).EnsureSuccessStatusCode();
        using var export = await alice.PostAsync(root + "/publish", null); export.EnsureSuccessStatusCode();
        Assert.Single(RegionDocument.Parse(Encoding.UTF8.GetBytes((await export.Content.ReadFromJsonAsync<RegionExport>())!.Json)));
    }
    [Fact]
    public async Task ExpiredDesignerCannotPublishAndCannotReleaseTheNewOwnersLock()
    {
        await using var host = new LockIntegrationTests.Host(); using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        string a = await Open(alice), b = await Open(bob);
        (await alice.PostAsync(a + "/locks/0", null)).EnsureSuccessStatusCode();
        var value = (await alice.GetFromJsonAsync<RegionRecord>(a + "/records/0"))!.Value;
        (await alice.PutAsJsonAsync(a + "/records/0", value)).EnsureSuccessStatusCode();
        host.Clock.Advance(TimeSpan.FromMinutes(3)); (await bob.PostAsync(b + "/locks/0", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(a + "/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsync(a + "/locks/0", null)).StatusCode);
        (await alice.DeleteAsync(a + "/locks/0?discard=true")).EnsureSuccessStatusCode();
        Assert.Equal("Bob", (await alice.GetFromJsonAsync<RegionRecord>(a + "/records/0"))!.Summary.Lock!.UserName);
        (await bob.DeleteAsync("/api/session")).EnsureSuccessStatusCode();
        Assert.Null((await alice.GetFromJsonAsync<RegionRecord>(a + "/records/0"))!.Summary.Lock);
    }
    [Fact]
    public async Task SourcesAreConfiguredAndCatalogsBelongToTheirSession()
    {
        await using var host = new LockIntegrationTests.Host(); using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob"); string root = await Open(alice);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/regions", new OpenRegionsRequest("../private"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync(root + "/records?limit=9999")).StatusCode);
        Assert.Single((await alice.GetFromJsonAsync<RegionPage>(root + "/records?map=Sea&search=harbor"))!.Items);
        using var created = await alice.PostAsync(root + "/records", null); created.EnsureSuccessStatusCode(); var record = (await created.Content.ReadFromJsonAsync<RegionRecord>())!;
        Assert.True(record.Summary.Lock!.Own);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.DeleteAsync(root)).StatusCode);
        (await alice.DeleteAsync(root + $"/locks/{record.Summary.Id}?discard=true")).EnsureSuccessStatusCode();
        (await alice.DeleteAsync(root)).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task PublishedIdsSurviveStoreRestartAndNewIdsAreNeverReused()
    {
        await using var host = new LockIntegrationTests.Host();
        string storage = Path.Combine(host.Data, "region-state"); var clock = new LockIntegrationTests.TestClock();
        var leases = new FiddlerWebApp.Services.Users.ResourceLeaseManager(clock);
        var first = new RegionStore(storage, leases);
        using (var workspace = first.Open(host.RegionFile, "Regions", "one", "Alice", [], []))
        {
            var region = workspace.Create(); workspace.Put(region.Summary.Id, region.Value); workspace.Publish(Path.Combine(host.Data, "region-exports"));
            workspace.Acquire(2); workspace.Delete(2); workspace.Publish(Path.Combine(host.Data, "region-exports"));
        }
        var restarted = new RegionStore(storage, leases);
        using var reopened = restarted.Open(host.RegionFile, "Regions", "two", "Bob", [], []);
        Assert.Equal(3, reopened.Read(3).Summary.Id); Assert.Throws<KeyNotFoundException>(() => reopened.Read(2));
        Assert.Equal(4, reopened.Create().Summary.Id);
    }
    [Fact]
    public void UnnamedRegionsAndTheirUnknownPropertiesRoundTripIndependently()
    {
        byte[] source = Encoding.UTF8.GetBytes("""
            [{"Map":"Felucca","Area":[{"x1":1,"y1":1,"x2":2,"y2":2}]},
             {"Map":"Felucca","Name":null,"Area":[{"x1":3,"y1":3,"x2":4,"y2":4}],"Special":{"value":42}}]
            """);
        var input = RegionDocument.Parse(source); var output = RegionDocument.Parse(RegionDocument.Export(input));
        Assert.Equal(2, output.Length); Assert.True(JsonElement.DeepEquals(input[0], output[0])); Assert.True(JsonElement.DeepEquals(input[1], output[1]));
    }
    [Fact]
    public async Task SourceChangesAbortPublicationAndPreserveDrafts()
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer"); string root = await Open(client);
        (await client.PostAsync(root + "/locks/0", null)).EnsureSuccessStatusCode();
        var record = (await client.GetFromJsonAsync<RegionRecord>(root + "/records/0"))!;
        (await client.PutAsJsonAsync(root + "/records/0", record.Value)).EnsureSuccessStatusCode();
        File.AppendAllText(host.RegionFile, " "); Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(root + "/publish", null)).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<RegionCatalog>(root))!.DraftCount);
    }
    [Fact]
    public async Task RegionViewModelPreservesSpecialFieldsAndShowsOtherDesigners()
    {
        await using var host = new LockIntegrationTests.Host(); using var aliceHttp = host.CreateClient(); using var bobHttp = host.CreateClient();
        var aliceApi = new AssetApiClient(aliceHttp);
        await using var alice = new WorkspaceViewModel(aliceApi) { DisplayName = "Alice" };
        var bobApi = new AssetApiClient(bobHttp); await using var bob = new WorkspaceViewModel(bobApi) { DisplayName = "Bob" };
        await alice.StartSessionCommand.ExecuteAsync(); await bob.StartSessionCommand.ExecuteAsync();
        await using var ar = new RegionsViewModel(aliceApi, alice); await using var br = new RegionsViewModel(bobApi, bob);
        await ar.SourcesCommand.ExecuteAsync(); ar.Source = "default"; await ar.OpenCommand.ExecuteAsync();
        await br.SourcesCommand.ExecuteAsync(); br.Source = "alias"; await br.OpenCommand.ExecuteAsync();
        await ar.SelectAsync(1); await ar.AcquireCommand.ExecuteAsync(); await br.SelectAsync(1);
        Assert.False(br.AcquireCommand.CanExecute(null)); Assert.False(br.SaveCommand.CanExecute(null)); Assert.Contains("Alice", br.Selected!.Summary.Lock!.UserName);
        ar.Priority = 123; ar.EditVolume(0, 5, "10"); ar.SetField("Visibility3DRadius", "12", true); await ar.SaveCommand.ExecuteAsync(); Assert.Null(ar.Error);
        Assert.Equal(123, ar.Selected!.Summary.Priority); Assert.Equal(10, ar.Area[0].Z2); Assert.Equal("12", ar.Field("Visibility3DRadius"));
        await ar.PublishCommand.ExecuteAsync(); Assert.Null(ar.Error); Assert.NotNull(ar.Exported);
    }
    [Theory]
    [InlineData("{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}", -128, 127)]
    [InlineData("[0,0,-10,10,10,20]", -10, 10)]
    [InlineData("{\"x\":0,\"y\":0,\"z\":-10,\"width\":10,\"height\":10,\"depth\":20}", -10, 10)]
    [InlineData("\"(0,0,-10)+(10,10,10)\"", -10, 10)]
    public void VolumeEncodingsMatchServerSemantics(string json, int z1, int z2)
    { using var doc = JsonDocument.Parse(json); var volume = RegionDocument.Volume(doc.RootElement); Assert.Equal(z1, volume.Z1); Assert.Equal(z2, volume.Z2); }
    [Fact]
    public void InvalidHierarchyAndVisibilityAreRejected()
    {
        static JsonElement Value(string parent, string name) => JsonSerializer.SerializeToElement(new { Name = name, Map = "Felucca", Parent = new { Name = parent, Map = "Felucca" }, Area = new[] { new { x1 = 0, y1 = 0, x2 = 10, y2 = 10 } } });
        Assert.Throws<InvalidOperationException>(() => RegionDocument.Ordered([Value("B", "A"), Value("A", "B")]));
        var value = JsonNode.Parse(Value("B", "A").GetRawText())!.AsObject(); value["Visibility3DRadius"] = 25;
        Assert.Throws<ArgumentException>(() => RegionDocument.Validate(JsonSerializer.SerializeToElement(value)));
    }
    [Fact]
    public void LocalServerDefinitionsRoundTripWhenAuditSourceIsConfigured()
    {
        string? file = Environment.GetEnvironmentVariable("UOAS_SERVER_REGIONS_AUDIT"); if (string.IsNullOrEmpty(file)) return;
        byte[] source = File.ReadAllBytes(file); var original = RegionDocument.Parse(source); var roundTrip = RegionDocument.Parse(RegionDocument.Export(original));
        Assert.NotEmpty(original); var remaining = roundTrip.ToList();
        foreach (var record in original) { int index = remaining.FindIndex(value => JsonElement.DeepEquals(record, value)); Assert.True(index >= 0, "A source property changed during export."); remaining.RemoveAt(index); } Assert.Empty(remaining);
        Assert.Equal(source, File.ReadAllBytes(file));
    }
}
