using System.Text.Json;
using System.ComponentModel;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed record AnimationAtlas(int FrameWidth, int FrameHeight, int FrameCount, int Columns, string Image);
public sealed record DiagnosticEvent(long Sequence, DateTimeOffset Timestamp, int Level, string Category, string Message, string MessageTemplate, string? Exception, string Properties);

public sealed class ToolViewModel : ObservableViewModel, IDisposable
{
    private readonly IAssetApi api;
    private readonly WorkspaceViewModel workspace;
    private Guid? workspaceId;
    public ToolViewModel(IAssetApi api, WorkspaceViewModel workspace)
    {
        this.api = api; this.workspace = workspace; workspaceId = workspace.Workspace?.Id; workspace.PropertyChanged += WorkspaceChanged;
        LoadCommand = new(() => Run(Load), () => !Busy && workspace.Workspace is not null);
        LogsCommand = new(() => Run(LoadLogs), () => !Busy && workspace.HasSession);
        ScanCommand = new(() => Run(async () => { var json = await api.GetAsync<JsonElement>(workspace.WorkspacePath + "/files"); Inventory = json.GetProperty("groups").EnumerateArray().SelectMany(group => group.GetProperty("files").EnumerateArray().Select(file => new InventoryFile(group.GetProperty("definition").GetProperty("name").GetString()!, file.GetProperty("name").GetString()!, file.GetProperty("status").GetInt32(), file.GetProperty("size").ValueKind == JsonValueKind.Number ? file.GetProperty("size").GetInt64() : null, file.GetProperty("detail").GetString()))).ToArray(); Output = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }); }), () => !Busy && workspace.Workspace is not null);
    }
    public AsyncCommand LoadCommand { get; }
    public AsyncCommand LogsCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public int Id { get; set; }
    public int RowSize { get; set; } = 16;
    public byte[]? Png { get; private set; }
    public string? Error { get; private set; }
    public string? Output { get; private set; }
    public IReadOnlyList<InventoryFile> Inventory { get; private set; } = [];
    public bool Busy { get; private set; }
    public int LogLevel { get; set; }
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public bool ExceptionsOnly { get; set; }
    public DateTime? From { get; set; }
    public DateTime? Until { get; set; }
    public int LogLimit { get; set; } = 200;
    public long AfterSequence { get; private set; }
    public DiagnosticStatus? Status { get; private set; }
    public IReadOnlyList<DiagnosticEvent> Logs { get; private set; } = [];
    private async Task Run(Func<Task> action)
    {
        Busy = true; Error = null; ChangedAll();
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or FormatException) { Error = ex.Message; }
        finally { Busy = false; ChangedAll(); LoadCommand.Notify(); LogsCommand.Notify(); ScanCommand.Notify(); }
    }
    private async Task Load()
    {
        Png = await api.ImageAsync($"{workspace.WorkspacePath}/tools/multi?id={Id}&rowSize={RowSize}");
    }
    private async Task LoadLogs()
    {
        string Time(string key, DateTime? value) => value is null ? "" : $"&{key}={Uri.EscapeDataString(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O"))}";
        Logs = await api.GetAsync<DiagnosticEvent[]>($"api/diagnostics?level={LogLevel}&category={Uri.EscapeDataString(Category)}&message={Uri.EscapeDataString(Message)}&exceptionsOnly={ExceptionsOnly.ToString().ToLowerInvariant()}&afterSequence={AfterSequence}&limit={LogLimit}" + Time("from", From) + Time("until", Until));
        Status = await api.GetAsync<DiagnosticStatus>("api/diagnostics/status");
    }
    public Task ClearLogsAsync() => Run(async () => { Status = await api.GetAsync<DiagnosticStatus>("api/diagnostics/status"); AfterSequence = Status.LatestSequence; Logs = []; });
    public Task ResetFiltersAsync() => Run(async () => { LogLevel = 0; Category = Message = ""; ExceptionsOnly = false; From = Until = null; AfterSequence = 0; await LoadLogs(); });
    public Task SetCaptureLevelAsync() => Run(() => api.PostAsync($"api/diagnostics/level/{LogLevel}"));
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (workspaceId != workspace.Workspace?.Id) { workspaceId = workspace.Workspace?.Id; Png = null; Output = null; Inventory = []; }
        ChangedAll(); LoadCommand.Notify(); LogsCommand.Notify(); ScanCommand.Notify();
    }
    public void Dispose() => workspace.PropertyChanged -= WorkspaceChanged;
}
public sealed record DiagnosticStatus(long LatestSequence, int Capacity, int CaptureLevel);
public sealed record InventoryFile(string Group, string Name, int Status, long? Size, string? Detail);
