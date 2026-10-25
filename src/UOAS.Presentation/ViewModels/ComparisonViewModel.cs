using System.ComponentModel;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed class ComparisonViewModel : ObservableViewModel, IDisposable
{
    private readonly IAssetApi api;
    private readonly WorkspaceViewModel workspace;
    private Guid? workspaceId;
    public ComparisonViewModel(IAssetApi api, WorkspaceViewModel workspace)
    {
        this.api = api; this.workspace = workspace; workspaceId = workspace.Workspace?.Id; workspace.PropertyChanged += WorkspaceChanged;
        CompareCommand = new(() => Run(Compare), () => !Busy && workspace.Workspace is not null && Library.Length > 0);
        AcquireCommand = new(() => Run(async () => { await api.PostAsync<ResourceLock>($"{workspace.WorkspacePath}/locks/Gump/{TargetId}"); await workspace.RefreshLocksAsync(); }), () => !Busy && workspace.LiveLocksKnown && Selected is not null && Owner is null);
        CopyCommand = new(() => Run(async () => { await api.PostAsync<AssetDetails>(Path + "/copy", new CopyGumpRequest(Selected!.Id, TargetId)); await workspace.RefreshCommand.ExecuteAsync(); await LoadTarget(); }), () => !Busy && workspace.LiveLocksKnown && Owner?.Own == true && Selected?.Difference is not (null or "CurrentOnly" or "Invalid"));
        NextCommand = new(() => Run(async () => { Page++; await LoadPage(); }), () => !Busy && Page + 1 < PageCount);
        PreviousCommand = new(() => Run(async () => { Page--; await LoadPage(); }), () => !Busy && Page > 0);
    }
    public AsyncCommand CompareCommand { get; }
    public AsyncCommand AcquireCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public AsyncCommand NextCommand { get; }
    public AsyncCommand PreviousCommand { get; }
    public IReadOnlyList<AssetLibrary> Libraries => workspace.Libraries;
    public string Library { get; set; } = "";
    public bool DifferencesOnly { get; set; } = true;
    public int Page { get; private set; }
    public int PageCount => Math.Max(1, (Result?.Total + 23 ?? 0) / 24);
    public ComparisonPage? Result { get; private set; }
    public ComparisonRow? Selected { get; private set; }
    public int TargetId { get; set; }
    public ResourceLock? Owner => workspace.Locks.FirstOrDefault(owner => owner.Kind == ResourceKind.Gump && owner.Id == TargetId);
    public byte[]? CurrentPng { get; private set; }
    public byte[]? ReferencePng { get; private set; }
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    private string Path => workspace.WorkspacePath + "/comparison";
    private void Notify() { ChangedAll(); CompareCommand.Notify(); AcquireCommand.Notify(); CopyCommand.Notify(); NextCommand.Notify(); PreviousCommand.Notify(); }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    { if (workspaceId != workspace.Workspace?.Id) { workspaceId = workspace.Workspace?.Id; Result = null; Selected = null; CurrentPng = ReferencePng = null; } Notify(); }
    private async Task Run(Func<Task> action)
    { if (Busy) return; Busy = true; Error = null; Notify(); try { await action(); } catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException) { Error = ex.Message; } finally { Busy = false; Notify(); } }
    private async Task Compare() { Result = await api.PostAsync<ComparisonPage>(Path, new CompareRequest(Library)); Page = 0; Selected = null; if (!DifferencesOnly) await LoadPage(); }
    private async Task LoadPage() => Result = await api.GetAsync<ComparisonPage>($"{Path}?offset={Page * 24}&limit=24&differencesOnly={DifferencesOnly.ToString().ToLowerInvariant()}");
    public Task FilterAsync() => Run(async () => { Page = 0; await LoadPage(); });
    public Task SelectAsync(ComparisonRow row) => Run(async () =>
    {
        Selected = row; TargetId = row.Id; CurrentPng = ReferencePng = null;
        await LoadTarget();
        if (row.Difference is not ("CurrentOnly" or "Invalid")) ReferencePng = await api.ImageAsync($"{Path}/reference/{row.Id}/image");
    });
    public Task TargetChangedAsync() => Run(LoadTarget);
    private async Task LoadTarget()
    {
        var current = await api.GetAsync<AssetDetails>($"{workspace.WorkspacePath}/assets/Gump/{TargetId}");
        CurrentPng = current.Empty ? null : await api.ImageAsync($"{workspace.WorkspacePath}/assets/Gump/{TargetId}/image");
        await workspace.RefreshLocksAsync();
    }
    public void Dispose() => workspace.PropertyChanged -= WorkspaceChanged;
}
