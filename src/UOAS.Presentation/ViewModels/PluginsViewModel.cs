using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed class PluginsViewModel(IAssetApi api, WorkspaceViewModel workspace) : ObservableViewModel
{
    public IReadOnlyList<PluginPackage> Packages { get; private set; } = [];
    public string EntryAssembly { get; set; } = "";
    public ResourceKind Kind { get; set; } = ResourceKind.Gump;
    public int Id { get; set; }
    public string? Error { get; private set; }
    public string? Output { get; private set; }
    public bool Busy { get; private set; }
    private async Task Run(Func<Task> action) { if (Busy) return; Busy = true; Error = null; ChangedAll(); try { await action(); } catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException) { Error = ex.Message; } finally { Busy = false; ChangedAll(); } }
    private async Task Reload() => Packages = await api.GetAsync<PluginPackage[]>("api/plugins");
    public Task RefreshAsync() => Run(Reload);
    public Task InstallAsync(string fileName, byte[] bytes) => Run(async () => { await api.PostAsync<PluginPackage>("api/plugins", new InstallPluginRequest(fileName, EntryAssembly, Convert.ToBase64String(bytes))); await Reload(); });
    public Task ActivateAsync(PluginPackage package) => Run(async () => { await api.PostAsync<PluginPackage>($"api/plugins/{package.Key}/activate"); await Reload(); });
    public Task DeactivateAsync(PluginPackage package) => Run(async () => { await api.PostAsync($"api/plugins/{package.Key}/deactivate"); await Reload(); });
    public Task RemoveAsync(PluginPackage package) => Run(async () => { await api.DeleteAsync($"api/plugins/{package.Key}"); await Reload(); });
    public Task ExecuteAsync(PluginPackage package, string command) => Run(async () => { if (workspace.Workspace is null) throw new InvalidOperationException("Open an asset library first."); Output = (await api.PostAsync<PluginOutput>($"api/plugins/{package.Key}/execute", new ExecutePluginRequest(workspace.Workspace.Id, Kind, Id, command))).Text; });
}
