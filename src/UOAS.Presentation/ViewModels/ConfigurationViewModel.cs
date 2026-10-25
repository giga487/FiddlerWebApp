using System.Text.Json;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed class LibrarySettingsDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
}
public sealed class RegionSettingsDraft
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public IReadOnlyList<string> Maps { get; set; } = [];
    public IReadOnlyList<string> Types { get; set; } = [];
}
public sealed class ConfigurationViewModel(IAssetApi api, WorkspaceViewModel workspace) : ObservableViewModel
{
    public bool Busy { get; private set; }
    public bool RootConfigured { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public long Revision { get; private set; }
    public StoragePage? Folder { get; private set; }
    public string CurrentPath => Folder?.Path ?? "";
    public int Page { get; private set; }
    public int PageCount => Math.Max(1, ((Folder?.Total ?? 0) + 49) / 50);
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
        if (RootConfigured) await Browse("", 0);
    });
    private void Apply(WorkspaceSettings settings)
    {
        Revision = settings.Revision; ExportDirectory = settings.ExportDirectory;
        Libraries = settings.Libraries.Select(item => new LibrarySettingsDraft { Id = item.Id, Name = item.Name, Directory = item.Directory }).ToList();
        Regions = settings.RegionSources.Select(item => new RegionSettingsDraft { Id = item.Id, Name = item.Name, File = item.File, Maps = item.Maps, Types = item.Types }).ToList();
    }
    private async Task Browse(string path, int page)
    { var result = await api.GetAsync<StoragePage>($"api/storage/entries?path={Uri.EscapeDataString(path)}&offset={page * 50}&limit=50"); Folder = result; Page = page; }
    public Task NavigateAsync(string path) => Run(() => Browse(path, 0));
    public Task NextAsync() => Run(() => Browse(CurrentPath, Math.Min(PageCount - 1, Page + 1)));
    public Task PreviousAsync() => Run(() => Browse(CurrentPath, Math.Max(0, Page - 1)));
    public void AddLibrary()
    {
        if (!RootConfigured || Busy || Libraries.Count >= 32) return;
        Libraries.Add(new() { Id = "library-" + Guid.NewGuid().ToString("N")[..8], Name = CurrentPath.Split('/').LastOrDefault(part => part.Length > 0) ?? "Asset library", Directory = CurrentPath }); ChangedAll();
    }
    public void AddRegions(string file)
    {
        if (!RootConfigured || Busy || Regions.Count >= 32) return;
        Regions.Add(new() { Id = "regions-" + Guid.NewGuid().ToString("N")[..8], Name = "Server regions", File = file }); ChangedAll();
    }
    public void UseFolderForRegions() => AddRegions(CurrentPath.Length == 0 ? "regions.json" : CurrentPath + "/regions.json");
    public void UseExportFolder() { ExportDirectory = CurrentPath; ChangedAll(); }
    public void Remove(LibrarySettingsDraft item) { Libraries.Remove(item); ChangedAll(); }
    public void Remove(RegionSettingsDraft item) { Regions.Remove(item); ChangedAll(); }
    public Task SaveAsync() => Run(async () =>
    {
        var settings = new WorkspaceSettings(Revision, Libraries.Select(item => new ConfiguredLibrary(item.Id, item.Name, item.Directory)).ToArray(), Regions.Select(item => new ConfiguredRegionSource(item.Id, item.Name, item.File, item.Maps, item.Types)).ToArray(), ExportDirectory);
        await api.PutAsync("api/configuration", settings);
        // Keep the revision acknowledged by our write if another designer saves immediately afterwards.
        Revision++; Notice = $"Configuration revision {Revision} saved. Sources are available when opening new workspaces; existing drafts remain in their current workspace.";
        await workspace.ReloadLibrariesAsync();
    });
}
