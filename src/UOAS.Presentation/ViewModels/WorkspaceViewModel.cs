using System.Globalization;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed record AssetTile(AssetSlot Slot, byte[]? Png, string? Error = null);

public sealed class WorkspaceViewModel : ObservableViewModel, IAsyncDisposable
{
    private readonly IAssetApi api;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly List<AsyncCommand> commands = [];
    private Task? polling;
    public WorkspaceViewModel(IAssetApi api)
    {
        this.api = api;
        StartSessionCommand = Command(StartSession, () => !HasSession);
        OpenCommand = Command(OpenWorkspace, () => HasSession && SelectedLibrary.Length > 0);
        RefreshCommand = Command(RefreshWorkspace, () => Workspace is not null);
        AcquireCommand = Command(Acquire, () => LiveLocksKnown && Selected is { Lock: null });
        ReleaseCommand = Command(() => Release(false), () => OwnsSelection && Selected?.Draft != true);
        DiscardCommand = Command(() => Release(true), () => Selected is not null && (OwnsSelection || Selected.Draft));
        RemoveCommand = Command(Remove, () => OwnsSelection && Kind != ResourceKind.Gump);
        PublishCommand = Command(Publish, () => Kind == ResourceKind.Gump ? Workspace?.GumpDraftCount > 0 : Workspace?.ArtDraftCount > 0);
        NextCommand = Command(async () => { Page++; await LoadPage(); }, () => Page + 1 < PageCount);
        PreviousCommand = Command(async () => { Page--; await LoadPage(); }, () => Page > 0);
        FindCommand = Command(async () => { Page = 0; await LoadPage(); }, () => Workspace is not null);
        CloseCommand = Command(CloseSession, () => HasSession);
    }
    private AsyncCommand Command(Func<Task> action, Func<bool> allowed)
    { var command = new AsyncCommand(() => Run(action), () => !Busy && allowed()); commands.Add(command); return command; }
    public AsyncCommand StartSessionCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AcquireCommand { get; }
    public AsyncCommand ReleaseCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand NextCommand { get; }
    public AsyncCommand PreviousCommand { get; }
    public AsyncCommand FindCommand { get; }
    public AsyncCommand CloseCommand { get; }
    public string DisplayName { get; set; } = "";
    public string SelectedLibrary { get; set; } = "";
    public IReadOnlyList<AssetLibrary> Libraries { get; private set; } = [];
    public WorkspaceState? Workspace { get; private set; }
    public bool HasSession => api.Session is not null;
    public string UserName => api.Session?.Name ?? "No session";
    public string WorkspacePath => Workspace is null ? throw new InvalidOperationException("Open a library first.") : $"api/workspaces/{Workspace.Id}";
    public ResourceKind Kind { get; set; } = ResourceKind.Gump;
    public bool ShowEmpty { get; set; } = true;
    public string SearchId { get; set; } = "";
    public int Page { get; private set; }
    public int PageCount { get; private set; } = 1;
    public int Total { get; private set; }
    public IReadOnlyList<AssetTile> Tiles { get; private set; } = [];
    public IReadOnlyList<ResourceLock> Locks { get; private set; } = [];
    public AssetDetails? Selected { get; private set; }
    public byte[]? SelectedPng { get; private set; }
    public string? PreviewError { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public bool Busy { get; private set; }
    public bool LiveLocksKnown { get; private set; } = true;
    public bool OwnsSelection => LiveLocksKnown && Selected?.Lock?.Own == true;
    public string LockStatus => !LiveLocksKnown ? "Lock status unavailable · reconnect before editing" : Selected?.Lock is not { } owner ? "Available for editing"
        : owner.Own ? $"Locked by you ({owner.UserName})" : $"Locked by {owner.UserName} · read only";
    public event Action? SessionStarted;
    private void Notify()
    { ChangedAll(); foreach (var command in commands) command.Notify(); }
    private async Task Run(Func<Task> action)
    {
        if (Busy) return;
        await operations.WaitAsync(lifetime.Token); Busy = true; Error = null; Notice = null; Notify();
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            Error = ex.Message;
            if (Workspace is not null)
            {
                try { await UpdateLocks(); } catch (HttpRequestException) { }
            }
        }
        finally { Busy = false; operations.Release(); Notify(); }
    }
    private async Task StartSession()
    {
        await api.StartSessionAsync(DisplayName, lifetime.Token);
        Libraries = await api.GetAsync<AssetLibrary[]>("api/libraries", lifetime.Token);
        SelectedLibrary = Libraries.FirstOrDefault()?.Id ?? "";
        SessionStarted?.Invoke();
        polling ??= Poll();
    }
    public Task ReloadLibrariesAsync() => Run(async () =>
    {
        Libraries = await api.GetAsync<AssetLibrary[]>("api/libraries", lifetime.Token);
        if (!Libraries.Any(library => library.Id == SelectedLibrary)) SelectedLibrary = Libraries.FirstOrDefault()?.Id ?? "";
    });
    private async Task CloseSession()
    {
        try { await api.EndSessionAsync(lifetime.Token); }
        finally { Workspace = null; Selected = null; SelectedPng = null; Tiles = []; Locks = []; Libraries = []; }
    }
    private async Task OpenWorkspace()
    {
        if (Workspace is not null)
        {
            if (Workspace.DraftCount > 0) throw new InvalidOperationException("Publish or discard your drafts before switching libraries.");
            await api.DeleteAsync(WorkspacePath, lifetime.Token);
            Workspace = null; Selected = null; SelectedPng = null; Tiles = []; Locks = [];
        }
        Workspace = await api.PostAsync<WorkspaceState>("api/workspaces", new OpenWorkspaceRequest(SelectedLibrary), lifetime.Token);
        Page = 0; Selected = null; SelectedPng = null; SearchId = ""; await LoadPage();
    }
    private async Task RefreshWorkspace()
    {
        Workspace = await api.PostAsync<WorkspaceState>(WorkspacePath + "/refresh", token: lifetime.Token);
        await LoadPage(); if (Selected is not null) await LoadSelection(Selected.Id);
    }
    public Task ChangeKindAsync() => Run(async () => { Selected = null; SelectedPng = null; Page = 0; SearchId = ""; await LoadPage(); });
    public Task ChangeFilterAsync() => Run(async () => { Page = 0; await LoadPage(); });
    private static int ParseId(string value)
    {
        bool valid = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id) : int.TryParse(value, out id);
        return valid && id >= 0 ? id : throw new ArgumentException("Enter a decimal ID or a hexadecimal ID prefixed with 0x.");
    }
    private async Task LoadPage()
    {
        if (Workspace is null) return;
        string search = string.IsNullOrWhiteSpace(SearchId) ? "" : "&id=" + ParseId(SearchId.Trim());
        var page = await api.GetAsync<AssetPage>($"{WorkspacePath}/assets/{Kind}?offset={Page * 24}&limit=24&includeEmpty={ShowEmpty.ToString().ToLowerInvariant()}{search}", lifetime.Token);
        Total = page.Total; PageCount = Math.Max(1, (page.Total + 23) / 24);
        var tiles = new List<AssetTile>();
        foreach (var slot in page.Items)
        {
            if (slot.Empty) { tiles.Add(new(slot, null)); continue; }
            try { tiles.Add(new(slot, await api.ImageAsync($"{WorkspacePath}/assets/{Kind}/{slot.Id}/image?thumbnail=true", lifetime.Token))); }
            catch (HttpRequestException ex) { tiles.Add(new(slot, null, ex.Message)); }
        }
        Tiles = tiles; await UpdateLocks();
    }
    public Task SelectAsync(int id) => Run(() => LoadSelection(id));
    public Task RefreshLocksAsync() => Run(UpdateLocks);
    private async Task LoadSelection(int id)
    {
        Selected = await api.GetAsync<AssetDetails>($"{WorkspacePath}/assets/{Kind}/{id}", lifetime.Token);
        SelectedPng = null; PreviewError = null;
        if (!Selected.Empty)
        {
            try { SelectedPng = await api.ImageAsync($"{WorkspacePath}/assets/{Kind}/{id}/image", lifetime.Token); }
            catch (HttpRequestException ex) { PreviewError = ex.Message; }
        }
    }
    private async Task Acquire()
    {
        int id = Selected!.Id;
        await api.PostAsync<ResourceLock>($"{WorkspacePath}/locks/{Kind}/{id}", token: lifetime.Token);
        await LoadSelection(id); await UpdateLocks();
    }
    private async Task Release(bool discard)
    {
        int id = Selected!.Id;
        await api.DeleteAsync($"{WorkspacePath}/locks/{Kind}/{id}?discard={discard.ToString().ToLowerInvariant()}", lifetime.Token);
        Workspace = await api.GetAsync<WorkspaceState>(WorkspacePath, lifetime.Token);
        await LoadPage(); await LoadSelection(id);
    }
    public Task UploadAsync(byte[] png) => Run(async () =>
    {
        if (!OwnsSelection) throw new InvalidOperationException("Acquire this ID before importing an image.");
        int id = Selected!.Id;
        await api.UploadAsync($"{WorkspacePath}/assets/{Kind}/{id}/image", png, lifetime.Token);
        Workspace = await api.GetAsync<WorkspaceState>(WorkspacePath, lifetime.Token);
        await LoadPage(); await LoadSelection(id);
    });
    private async Task Remove()
    {
        int id = Selected!.Id;
        await api.DeleteAsync($"{WorkspacePath}/assets/{Kind}/{id}", lifetime.Token);
        Workspace = await api.GetAsync<WorkspaceState>(WorkspacePath, lifetime.Token);
        await LoadPage(); await LoadSelection(id);
    }
    private async Task Publish()
    {
        var result = await api.PostAsync<ExportResult>($"{WorkspacePath}/publish/{Kind}", token: lifetime.Token);
        Workspace = await api.GetAsync<WorkspaceState>(WorkspacePath, lifetime.Token);
        await LoadPage(); if (Selected is not null) await LoadSelection(Selected.Id);
        Notice = $"Published revision {result.Revision} to {result.Directory}.";
    }
    private async Task UpdateLocks()
    {
        Locks = await api.GetAsync<ResourceLock[]>(WorkspacePath + "/locks", lifetime.Token);
        LiveLocksKnown = true;
        Tiles = Tiles.Select(tile => tile with { Slot = tile.Slot with { Lock = Locks.FirstOrDefault(owner => owner.Kind == Kind && owner.Id == tile.Slot.Id) } }).ToArray();
        if (Selected is not null) Selected = Selected with { Lock = Locks.FirstOrDefault(owner => owner.Kind == Kind && owner.Id == Selected.Id) };
    }
    private async Task Poll()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        int ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (!HasSession || Workspace is null || Busy || !await operations.WaitAsync(0, lifetime.Token)) continue;
                try
                {
                    await UpdateLocks();
                    if (++ticks % 5 == 0) Workspace = await api.PostAsync<WorkspaceState>(WorkspacePath + "/heartbeat", token: lifetime.Token);
                    Notify();
                }
                catch (HttpRequestException ex) { LiveLocksKnown = false; Error = "Live lock status unavailable: " + ex.Message; Notify(); }
                finally { operations.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    { lifetime.Cancel(); if (polling is not null) await polling; lifetime.Dispose(); }
}
