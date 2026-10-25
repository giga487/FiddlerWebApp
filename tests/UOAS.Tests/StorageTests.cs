using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using UOAS.Application.Storage;
using UOAS.Contracts;
using Xunit;

namespace UOAS.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task BrowserListsRootAndNestedFoldersAndConfigurationChangesBecomeAvailable()
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer");
        Assert.True((await client.GetFromJsonAsync<StorageStatus>("/api/storage"))!.Configured);
        var root = await client.GetFromJsonAsync<StoragePage>("/api/storage/entries"); Assert.Equal("", root!.Path); Assert.Null(root.Parent);
        Assert.Contains(root.Entries, entry => entry.Name == "client" && entry.Directory && !entry.Blocked);
        var child = await client.GetFromJsonAsync<StoragePage>("/api/storage/entries?path=client&limit=2"); Assert.Equal("", child!.Parent); Assert.Equal(2, child.Entries.Count);
        var settings = (await client.GetFromJsonAsync<WorkspaceSettings>("/api/configuration"))!;
        Assert.Equal("client", settings.Libraries.Single(item => item.Id == "default").Directory);
        var changed = settings with { Libraries = [..settings.Libraries, new("another", "Another client", "client")] };
        (await client.PutAsJsonAsync("/api/configuration", changed)).EnsureSuccessStatusCode();
        Assert.Contains((await client.GetFromJsonAsync<AssetLibrary[]>("/api/libraries"))!, item => item.Id == "another");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/configuration", changed)).StatusCode);
        using var anon = host.CreateClient(); Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/api/storage/entries")).StatusCode);
    }
    [Theory]
    [InlineData("../")]
    [InlineData("client/../../outside")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    [InlineData("\\\\server\\share")]
    [InlineData("client:stream")]
    [InlineData("client/.. /outside")]
    public async Task BrowserRejectsPathsOutsideTheConfiguredRoot(string path)
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/storage/entries?path=" + Uri.EscapeDataString(path))).StatusCode);
    }
    [Fact]
    public async Task SettingsCannotChangeTheRootOrUseOutsideSourcesAndOutputs()
    {
        await using var host = new LockIntegrationTests.Host(); using var client = await host.Client("Designer");
        var settings = (await client.GetFromJsonAsync<WorkspaceSettings>("/api/configuration"))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/configuration", settings with { ExportDirectory = "../outside" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/configuration", settings with { Libraries = [new("bad", "Bad", "../outside")] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/configuration", settings with { RegionSources = [new("bad", "Bad", "../outside.json", [], [])] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/configuration", new { settings.Revision, settings.Libraries, settings.RegionSources, settings.ExportDirectory, RootDirectory = "C:/" })).StatusCode);
        Assert.Equal(settings.Revision, (await client.GetFromJsonAsync<WorkspaceSettings>("/api/configuration"))!.Revision);
    }
    [Fact]
    public async Task DirectoryLinksAreListedAsBlockedAndCannotBeOpenedAsLibrariesOrRegions()
    {
        await using var host = new LockIntegrationTests.Host();
        string outside = Path.Combine(Path.GetDirectoryName(host.Data)!, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "marker.txt"), "untouched");
        string link = Path.Combine(host.Data, "escape");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(outside);
            using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        var storage = new StorageRoot(host.Data, host.Data);
        Assert.True(storage.Browse("", 0, 100).Entries.Single(entry => entry.Name == "escape").Blocked);
        Assert.Throws<ArgumentException>(() => storage.Resolve("escape/marker.txt"));
        Assert.Throws<ArgumentException>(() => storage.ValidateLibrary(""));
        Assert.Throws<ArgumentException>(() => storage.FromConfiguredPath(outside));
        Directory.Delete(link); Assert.True(File.Exists(Path.Combine(outside, "marker.txt")));
    }
    [Fact]
    public async Task SettingsPersistRelativePathsAndBackupThePreviousRevision()
    {
        await using var host = new LockIntegrationTests.Host();
        var storage = new StorageRoot(host.Data, host.Data); string state = Path.Combine(host.Data, "settings-state");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assets:ExportDirectory"] = "Exports" }).Build();
        var first = new WorkspaceSettingsStore(storage, configuration, state);
        var initial = first.Read();
        var saved = first.Save(initial with { Libraries = [new("client", "Client", "")] }); Assert.Equal(1, saved.Revision);
        var reopened = new WorkspaceSettingsStore(storage, configuration, state); Assert.Equal(1, reopened.Read().Revision);
        Assert.Equal("", reopened.Read().Libraries.Single().Directory);
        reopened.Save(saved with { ExportDirectory = "Output" }); Assert.Single(Directory.GetFiles(state, "*.bak"));
        Assert.Throws<InvalidOperationException>(() => first.Save(initial));
        Assert.StartsWith(host.Data, storage.Resolve("Output", true));
    }
}
