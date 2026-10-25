using System.ComponentModel;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed record AnimationBodyPage(int Total, IReadOnlyList<AnimationBodyInfo> Items);
public sealed class AnimationViewModel : ObservableViewModel, IDisposable
{
    private readonly IAssetApi api;
    private readonly WorkspaceViewModel workspace;
    private Guid? workspaceId;
    private readonly List<AsyncCommand> commands = [];
    public AnimationViewModel(IAssetApi api, WorkspaceViewModel workspace)
    {
        this.api = api; this.workspace = workspace; workspaceId = workspace.Workspace?.Id; workspace.PropertyChanged += Updated;
        ArchivesCommand = Command(async () => { Archives = await api.GetAsync<AnimationArchive[]>(Path); File = Archives.FirstOrDefault()?.File ?? 1; Page = 0; await Search(); });
        SearchCommand = Command(async () => { Page = 0; await Search(); });
        PreviousCommand = Command(async () => { Page--; await Search(); }, () => Page > 0);
        NextCommand = Command(async () => { Page++; await Search(); }, () => Page + 1 < PageCount);
        LoadCommand = Command(Load, () => Actions.Any(action => action.Id == Action && action.Directions.Contains(Direction)));
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? allowed = null) { var command = new AsyncCommand(() => Run(action), () => !Busy && workspace.Workspace is not null && (allowed?.Invoke() ?? true)); commands.Add(command); return command; }
    public AsyncCommand ArchivesCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand PreviousCommand { get; }
    public AsyncCommand NextCommand { get; }
    public AsyncCommand LoadCommand { get; }
    public IReadOnlyList<AnimationArchive> Archives { get; private set; } = [];
    public IReadOnlyList<AnimationBodyInfo> Bodies { get; private set; } = [];
    public IReadOnlyList<AnimationActionInfo> Actions { get; private set; } = [];
    public IReadOnlyList<int> Directions => Actions.FirstOrDefault(action => action.Id == Action)?.Directions ?? [];
    public int File { get; set; } = 1;
    public string Category { get; set; } = "";
    public int? FindId { get; set; }
    public int Body { get; private set; }
    public int Action { get; set; }
    public int Direction { get; set; }
    public int Page { get; private set; }
    public int PageCount { get; private set; } = 1;
    public int Total { get; private set; }
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public byte[]? Png { get; private set; }
    public AnimationAtlas? Atlas { get; private set; }
    private string Path => workspace.WorkspacePath + "/tools/animations";
    private void Notify() { ChangedAll(); foreach (var command in commands) command.Notify(); }
    private void Updated(object? sender, PropertyChangedEventArgs args) { if (workspaceId != workspace.Workspace?.Id) { workspaceId = workspace.Workspace?.Id; Archives = []; Bodies = []; Actions = []; Atlas = null; Png = null; } Notify(); }
    private async Task Run(Func<Task> action) { if (Busy) return; Busy = true; Error = null; Notify(); try { await action(); } catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or FormatException) { Error = ex.Message; } finally { Busy = false; Notify(); } }
    private async Task Search()
    {
        Actions = []; Atlas = null; Png = null;
        if (Archives.Count == 0) { Bodies = []; return; }
        var result = await api.GetAsync<AnimationBodyPage>($"{Path}/{File}/bodies?category={Uri.EscapeDataString(Category)}&offset={Page * 24}&limit=24" + (FindId is int id ? $"&id={id}" : ""));
        Bodies = result.Items; Total = result.Total; PageCount = Math.Max(1, (Total + 23) / 24);
    }
    public Task SelectAsync(int body) => Run(async () => { Body = body; Atlas = null; Png = null; Actions = await api.GetAsync<AnimationActionInfo[]>($"{Path}/{File}/bodies/{body}/actions"); Action = Actions.FirstOrDefault()?.Id ?? 0; Direction = Directions.FirstOrDefault(); if (Actions.Count > 0) await Load(); });
    public Task ActionChangedAsync() => Run(async () => { Direction = Directions.FirstOrDefault(); await Load(); });
    public Task DirectionChangedAsync() => Run(Load);
    private async Task Load() { if (Directions.Count == 0) return; Atlas = await api.GetAsync<AnimationAtlas>($"{Path}/{File}/{Body}/{Action}/{Direction}"); Png = Convert.FromBase64String(Atlas.Image); }
    public void Dispose() => workspace.PropertyChanged -= Updated;
}
