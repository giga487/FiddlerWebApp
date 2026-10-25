using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UOAS.Contracts;
using Xunit;

namespace UOAS.Tests;

public sealed class DocumentIntegrationTests
{
    [Theory]
    [InlineData(DocumentKind.Hue, "hues.mul", 1)]
    [InlineData(DocumentKind.Font, "fonts.mul", 32)]
    [InlineData(DocumentKind.TileDataLand, "tiledata.mul", 10)]
    [InlineData(DocumentKind.TileDataItem, "tiledata.mul", 10)]
    public async Task NativeRecordEditorsRequirePerIdOwnershipAndPublishWithoutChangingTheirSource(DocumentKind kind, string file, int id)
    {
        await using var host = new LockIntegrationTests.Host();
        File.WriteAllBytes(Path.Combine(host.Data, "hues.mul"), new byte[708]);
        File.WriteAllBytes(Path.Combine(host.Data, "tiledata.mul"), new byte[512 * (4 + 32 * 26) + 4 + 32 * 37]);
        using (var fonts = new BinaryWriter(File.Create(Path.Combine(host.Data, "fonts.mul"))))
        { fonts.Write((byte)1); for (int code = 32; code < 256; code++) { fonts.Write((byte)1); fonts.Write((byte)1); fonts.Write((byte)0); fonts.Write((ushort)0x7c00); } }
        byte[] original = File.ReadAllBytes(Path.Combine(host.Data, file));
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        async Task<string> OpenDoc(HttpClient client)
        {
            using var w = await client.PostAsJsonAsync("/api/workspaces", new OpenWorkspaceRequest("default")); w.EnsureSuccessStatusCode();
            string root = $"/api/workspaces/{(await w.Content.ReadFromJsonAsync<WorkspaceState>())!.Id}/documents";
            using var opened = await client.PostAsJsonAsync(root, new OpenDocumentRequest(kind, file)); opened.EnsureSuccessStatusCode();
            return root + "/" + (await opened.Content.ReadFromJsonAsync<DocumentInfo>())!.Id;
        }
        string ar = await OpenDoc(alice), br = await OpenDoc(bob);
        var record = (await alice.GetFromJsonAsync<DocumentRecord>($"{ar}/records/{id}"))!;
        (await alice.PostAsync($"{ar}/locks/{id}", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync($"{br}/locks/{id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutAsJsonAsync($"{br}/records/{id}", record.Value)).StatusCode);
        (await alice.PutAsJsonAsync($"{ar}/records/{id}", record.Value)).EnsureSuccessStatusCode();
        using var published = await alice.PostAsync(ar + "/publish", null); published.EnsureSuccessStatusCode();
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(host.Data, file)));
        (await bob.PostAsync($"{br}/locks/{id}", null)).EnsureSuccessStatusCode();
        Assert.True((await bob.GetFromJsonAsync<DocumentRecord>($"{br}/records/{id}"))!.Lock!.Own);
    }

    private static async Task<(string Workspace, string Document)> Open(HttpClient client)
    {
        using var workspace = await client.PostAsJsonAsync("/api/workspaces", new OpenWorkspaceRequest("default")); workspace.EnsureSuccessStatusCode();
        var root = $"/api/workspaces/{(await workspace.Content.ReadFromJsonAsync<WorkspaceState>())!.Id}";
        using var document = await client.PostAsJsonAsync(root + "/documents", new OpenDocumentRequest(DocumentKind.Cliloc, "cliloc.enu")); document.EnsureSuccessStatusCode();
        return (root, root + "/documents/" + (await document.Content.ReadFromJsonAsync<DocumentInfo>())!.Id);
    }
    [Fact]
    public async Task RecordLocksAreEnforcedAcrossUsersAndDifferentRecordChangesAreMerged()
    {
        await using var host = new LockIntegrationTests.Host();
        using (var writer = new BinaryWriter(File.Create(Path.Combine(host.Data, "cliloc.enu"))))
        { writer.Write(new byte[6]); writer.Write(10); writer.Write((byte)0); writer.Write((ushort)3); writer.Write("Old"u8.ToArray()); }
        using var alice = await host.Client("Alice"); using var bob = await host.Client("Bob");
        var a = await Open(alice); var b = await Open(bob);
        (await alice.PostAsync(a.Document + "/locks/10", null)).EnsureSuccessStatusCode();
        using var blocked = await bob.PostAsync(b.Document + "/locks/10", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode); Assert.Contains("Alice", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutAsJsonAsync(b.Document + "/records/10", new { text = "Denied", flag = 0 })).StatusCode);
        (await alice.PutAsJsonAsync(a.Document + "/records/10", new { text = "Alice's text", flag = 0 })).EnsureSuccessStatusCode();
        (await bob.PostAsync(b.Document + "/locks/11", null)).EnsureSuccessStatusCode();
        (await bob.PutAsJsonAsync(b.Document + "/records/11", new { text = "Bob's text", flag = 0 })).EnsureSuccessStatusCode();
        (await bob.PostAsync(b.Document + "/publish", null)).EnsureSuccessStatusCode();
        (await alice.PostAsync(a.Document + "/publish", null)).EnsureSuccessStatusCode();
        (await bob.PostAsync(b.Document + "/refresh", null)).EnsureSuccessStatusCode();
        Assert.Equal("Alice's text", (await bob.GetFromJsonAsync<DocumentRecord>(b.Document + "/records/10"))!.Value.GetProperty("text").GetString());
        Assert.Equal("Bob's text", (await bob.GetFromJsonAsync<DocumentRecord>(b.Document + "/records/11"))!.Value.GetProperty("text").GetString());
        Assert.Null((await bob.GetFromJsonAsync<DocumentRecord>(b.Document + "/records/10"))!.Lock);
        using var invalid = await alice.PostAsJsonAsync(a.Workspace + "/documents", new OpenDocumentRequest(DocumentKind.Cliloc, "../cliloc.enu"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
