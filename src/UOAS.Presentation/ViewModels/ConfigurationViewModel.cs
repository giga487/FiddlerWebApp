using System.Text.Json;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed class LibrarySettingsDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public List<MapSettingsDraft> Maps { get; set; } = [];
}
public sealed class MapSettingsDraft
{
    public int FileId { get; set; }
    public string Name { get; set; } = "Custom map";
    public int Width { get; set; }
    public int Height { get; set; }
}
public sealed class RegionSettingsDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public IReadOnlyList<string> Maps { get; set; } = [];
    public IReadOnlyList<string> Types { get; set; } = [];
}
public sealed class StorageBrowserNode(StorageEntry entry)
{
    public StorageEntry Entry { get; } = entry;
    public bool Expanded { get; set; }
    public bool Loaded { get; set; }
    public int Total { get; set; }
    public List<StorageBrowserNode> Children { get; } = [];
}
public sealed class ConfigurationViewModel(IAssetApi api, WorkspaceViewModel workspace) : ObservableViewModel
{
    public bool Busy { get; private set; }
    public bool RootConfigured { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public long Revision { get; private set; }
    public StoragePage? Folder { get; private set; }
    public StorageBrowserNode RootNode { get; private set; } = NewRoot();
    public StorageEntry? SelectedEntry { get; private set; }
    public bool SelectedFolder => SelectedEntry is { Directory: true, Blocked: false };
    public bool SelectedRegionFile => SelectedEntry is { Directory: false, Blocked: false } entry && entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    public IEnumerable<(string Name, string Path)> Breadcrumbs
    {
        get
        {
            yield return ("Storage root", "");
            string path = "";
            foreach (string part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            { path = path.Length == 0 ? part : path + "/" + part; yield return (part, path); }
        }
    }
    public string CurrentPath => Folder?.Path ?? "";
    public List<LibrarySettingsDraft> Libraries { get; private set; } = [];
    public List<RegionSettingsDraft> Regions { get; private set; } = [];
    public string ExportDirectory { get; set; } = "Exports";
    private async Task Run(Func<Task> action)
    {
        if (Busy) return; Busy = true; Error = null; ChangedAll();
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or JsonException) { Error = ex.Message; }
        finally { Busy = false; ChangedAll(); }
    }
    public Task LoadAsync() => Run(async () =>
    {
        Notice = null; RootConfigured = (await api.GetAsync<StorageStatus>("api/storage")).Configured;
        Apply(await api.GetAsync<WorkspaceSettings>("api/configuration"));
        RootNode = NewRoot(); SelectedEntry = null; Folder = null;
        if (RootConfigured) await Browse("");
    });
    private void Apply(WorkspaceSettings settings)
    {
        Revision = settings.Revision; ExportDirectory = settings.ExportDirectory;
        Libraries = settings.Libraries.Select(item => new LibrarySettingsDraft { Id = item.Id, Name = item.Name, Directory = item.Directory, Maps = item.Maps?.Select(map => new MapSettingsDraft { FileId = map.FileId, Name = map.Name, Width = map.Width, Height = map.Height }).ToList() ?? [] }).ToList();
        Regions = settings.RegionSources.Select(item => new RegionSettingsDraft { Id = item.Id, Name = item.Name, File = item.File, Maps = item.Maps, Types = item.Types }).ToList();
    }
    private static StorageBrowserNode NewRoot() => new(new("Storage root", "", true, false, null));
    private StorageBrowserNode Node(string path)
    {
        var node = RootNode;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string childPath = node.Entry.Path.Length == 0 ? part : node.Entry.Path + "/" + part;
            var child = node.Children.FirstOrDefault(item => item.Entry.Path == childPath);
            if (child is null) { child = new(new(part, childPath, true, false, null)); node.Children.Add(child); }
            node.Expanded = true; node = child;
        }
        return node;
    }
    private async Task<StoragePage> LoadChildren(StorageBrowserNode node, int offset)
    {
        var result = await api.GetAsync<StoragePage>($"api/storage/entries?path={Uri.EscapeDataString(node.Entry.Path)}&offset={offset}&limit=50");
        if (offset == 0) node.Children.Clear();
        node.Children.AddRange(result.Entries.Select(entry => new StorageBrowserNode(entry)));
        node.Total = result.Total; node.Loaded = true;
        return result;
    }
    private async Task Browse(string path)
    {
        var node = Node(path);
        // Preserve expanded descendants when returning to a previously opened folder.
        Folder = node.Loaded ? new(path, path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "", node.Total, node.Children.Select(child => child.Entry).ToArray()) : await LoadChildren(node, 0);
        node.Expanded = true; SelectedEntry = node.Entry;
    }
    public Task ToggleAsync(StorageBrowserNode node) => Run(async () =>
    {
        if (!node.Entry.Directory || node.Entry.Blocked) return;
        if (node.Expanded) { node.Expanded = false; return; }
        if (!node.Loaded) await LoadChildren(node, 0);
        node.Expanded = true;
    });
    public Task SelectAsync(StorageBrowserNode node) => Run(async () =>
    {
        if (node.Entry.Blocked) return;
        if (node.Entry.Directory) await Browse(node.Entry.Path);
        else
        {
            string path = node.Entry.Path;
            await Browse(path.Contains('/') ? path[..path.LastIndexOf('/')] : "");
            SelectedEntry = node.Entry;
        }
    });
    public Task LoadMoreAsync(StorageBrowserNode node) => Run(async () =>
    {
        if (!node.Loaded || node.Children.Count >= node.Total) return;
        await LoadChildren(node, node.Children.Count);
    });
    public Task NavigateAsync(string path) => Run(() => Browse(path));
    public void AddLibrary()
    {
        if (!RootConfigured || Busy || !SelectedFolder || Libraries.Count >= 32) return;
        Libraries.Add(new() { Id = "library-" + Guid.NewGuid().ToString("N")[..8], Name = CurrentPath.Split('/').LastOrDefault(part => part.Length > 0) ?? "Asset library", Directory = CurrentPath }); ChangedAll();
    }
    public void AddRegions(string file)
    {
        if (!RootConfigured || Busy || Regions.Count >= 32) return;
        Regions.Add(new() { Id = "regions-" + Guid.NewGuid().ToString("N")[..8], Name = "Server regions", File = file }); ChangedAll();
    }
    public void UseFolderForRegions() { if (SelectedFolder) AddRegions(CurrentPath.Length == 0 ? "regions.json" : CurrentPath + "/regions.json"); }
    public void UseExportFolder() { if (!SelectedFolder || Busy) return; ExportDirectory = CurrentPath; ChangedAll(); }
    public void Remove(LibrarySettingsDraft item) { Libraries.Remove(item); ChangedAll(); }
    public void AddMap(LibrarySettingsDraft library) { if (library.Maps.Count >= 256) return; library.Maps.Add(new() { FileId = Enumerable.Range(0, 256).First(id => library.Maps.All(map => map.FileId != id)) }); ChangedAll(); }
    public void RemoveMap(LibrarySettingsDraft library, MapSettingsDraft map) { library.Maps.Remove(map); ChangedAll(); }
    public void Remove(RegionSettingsDraft item) { Regions.Remove(item); ChangedAll(); }
    public Task SaveAsync() => Run(async () =>
    {
        var settings = new WorkspaceSettings(Revision, Libraries.Select(item => new ConfiguredLibrary(item.Id, item.Name, item.Directory, item.Maps.Select(map => new MapDefinition(map.FileId, map.Name, map.Width, map.Height)).ToArray())).ToArray(), Regions.Select(item => new ConfiguredRegionSource(item.Id, item.Name, item.File, item.Maps, item.Types)).ToArray(), ExportDirectory);
        await api.PutAsync("api/configuration", settings);
        // Keep the revision acknowledged by our write if another designer saves immediately afterwards.
        Revision++; Notice = $"Configuration revision {Revision} saved. Sources are available when opening new workspaces; existing drafts remain in their current workspace.";
        await workspace.ReloadLibrariesAsync();
    });
}
