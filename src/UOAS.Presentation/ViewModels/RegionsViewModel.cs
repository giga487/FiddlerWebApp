using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed class RegionsViewModel : ObservableViewModel, IAsyncDisposable
{
    private readonly IAssetApi api;
    private readonly WorkspaceViewModel session;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<AsyncCommand> commands = [];
    private Task? polling;
    public RegionsViewModel(IAssetApi api, WorkspaceViewModel session)
    {
        this.api = api; this.session = session; session.PropertyChanged += SessionChanged;
        SourcesCommand = Command(async () => { Sources = await api.GetAsync<RegionSource[]>("api/region-sources"); Source = Sources.FirstOrDefault()?.Id ?? ""; });
        OpenCommand = Command(async () => { Catalog = await api.PostAsync<RegionCatalog>("api/regions", new OpenRegionsRequest(Source)); Page = 0; await List(); polling ??= Poll(); }, () => Catalog is null && Source.Length > 0);
        CloseCommand = Command(async () => { await api.DeleteAsync(Path); Reset(); }, () => Catalog is not null && Catalog.DraftCount == 0);
        SearchCommand = Command(async () => { Page = 0; await List(); }, () => Catalog is not null);
        PreviousCommand = Command(async () => { Page--; await List(); }, () => Catalog is not null && Page > 0);
        NextCommand = Command(async () => { Page++; await List(); }, () => Catalog is not null && Page + 1 < PageCount);
        NewCommand = Command(async () => { Load(await api.PostAsync<RegionRecord>(Path + "/records")); await List(); }, () => Catalog is not null);
        AcquireCommand = Command(async () => { int id = Selected!.Summary.Id; await api.PostAsync<DocumentOwner>($"{Path}/locks/{id}"); Load(await api.GetAsync<RegionRecord>($"{Path}/records/{id}")); }, () => LiveLocksKnown && Selected is { Summary.Lock: null });
        SaveCommand = Command(async () => { await api.PutAsync($"{Path}/records/{Selected!.Summary.Id}", Build()); Load(await api.GetAsync<RegionRecord>($"{Path}/records/{Selected.Summary.Id}")); await List(); }, () => OwnsSelection);
        DeleteCommand = Command(async () => { await api.DeleteAsync($"{Path}/records/{Selected!.Summary.Id}"); Selected = null; await List(); }, () => OwnsSelection);
        UndoCommand = Command(async () => { int id = Selected!.Summary.Id; await api.DeleteAsync($"{Path}/locks/{id}?discard=true"); Selected = null; await List(); }, () => Selected is not null && (OwnsSelection || Selected.Summary.Draft));
        ReleaseCommand = Command(async () => { int id = Selected!.Summary.Id; await api.DeleteAsync($"{Path}/locks/{id}"); Load(await api.GetAsync<RegionRecord>($"{Path}/records/{id}")); await List(); }, () => OwnsSelection && Selected?.Summary.Draft != true);
        RefreshCommand = Command(async () => { Catalog = await api.PostAsync<RegionCatalog>(Path + "/refresh"); Selected = null; await List(); }, () => Catalog is not null);
        PublishCommand = Command(async () => { Exported = await api.PostAsync<RegionExport>(Path + "/publish"); Notice = $"Published region revision {Exported.Revision}. Download regions.json and deploy it through your server's normal release process; it loads on server startup."; Selected = null; await List(); }, () => Catalog?.DraftCount > 0);
        ExportCommand = Command(async () => { Exported = await api.GetAsync<RegionExport>(Path + "/export"); }, () => Catalog is not null);
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? allowed = null) { var command = new AsyncCommand(() => Run(action), () => !Busy && session.HasSession && (allowed?.Invoke() ?? true)); commands.Add(command); return command; }
    public AsyncCommand SourcesCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand CloseCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand PreviousCommand { get; }
    public AsyncCommand NextCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand AcquireCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand UndoCommand { get; }
    public AsyncCommand ReleaseCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public IReadOnlyList<RegionSource> Sources { get; private set; } = [];
    public string Source { get; set; } = "";
    public RegionCatalog? Catalog { get; private set; }
    public IReadOnlyList<RegionSummary> Rows { get; private set; } = [];
    public RegionRecord? Selected { get; private set; }
    public IReadOnlyList<RegionVolume> Area { get; private set; } = [];
    public RegionExport? Exported { get; private set; }
    public string Search { get; set; } = "";
    public string MapFilter { get; set; } = "";
    public int Page { get; private set; }
    public int PageCount { get; private set; } = 1;
    public bool Busy { get; private set; }
    public bool LiveLocksKnown { get; private set; } = true;
    public bool OwnsSelection => LiveLocksKnown && Selected is { Summary.Lock.Own: true, Summary.Deleted: false };
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public string Type { get; set; } = "BaseRegion";
    public int Priority { get; set; } = 50;
    public string ParentName { get; set; } = "";
    public string ParentMap { get; set; } = "";
    public string JsonText { get; set; } = "{}";
    public string OriginalJson { get; private set; } = "{}";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string Path => "api/regions/" + (Catalog?.Id ?? throw new InvalidOperationException("Open a region source first."));
    private void Notify() { ChangedAll(); foreach (var command in commands) command.Notify(); }
    private async Task Run(Func<Task> action)
    {
        if (Busy) return; Busy = true; Error = null; Notify();
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or JsonException or FormatException or OverflowException) { Error = ex.Message; }
        finally { Busy = false; Notify(); }
    }
    private async Task List()
    {
        var result = await api.GetAsync<RegionPage>($"{Path}/records?offset={Page * 24}&limit=24&map={Uri.EscapeDataString(MapFilter)}&search={Uri.EscapeDataString(Search)}");
        Rows = result.Items; PageCount = Math.Max(1, (result.Total + 23) / 24); Catalog = await api.GetAsync<RegionCatalog>(Path);
    }
    public Task SelectAsync(int id) => Run(async () => Load(await api.GetAsync<RegionRecord>($"{Path}/records/{id}")));
    private void Load(RegionRecord record)
    {
        Selected = record; Name = record.Summary.Name; Map = record.Summary.Map; Type = record.Summary.Type; Priority = record.Summary.Priority;
        ParentName = record.Summary.ParentName ?? ""; ParentMap = record.Value.TryGetProperty("Parent", out var parent) && parent.ValueKind == JsonValueKind.Object ? parent.GetProperty("Map").GetString()! : Map;
        Area = record.Area; JsonText = OriginalJson = JsonSerializer.Serialize(record.Value, Json);
    }
    public JsonElement Build()
    {
        var value = JsonNode.Parse(JsonText)?.AsObject() ?? throw new ArgumentException("Provide a region JSON object.");
        value["$type"] = Type; if (Name.Length > 0) value["Name"] = Name; else value.Remove("Name"); value["Map"] = Map; value["Priority"] = Priority;
        if (ParentName.Length == 0) value.Remove("Parent"); else value["Parent"] = new JsonObject { ["Name"] = ParentName, ["Map"] = ParentMap };
        return JsonSerializer.SerializeToElement(value);
    }
    private JsonObject? Form()
    { try { return JsonNode.Parse(JsonText) as JsonObject; } catch (JsonException) { return null; } }
    public string Field(string key) => Form()?[key]?.ToString() ?? "";
    public void SetField(string key, string? input, bool numeric = false)
    {
        if (!OwnsSelection) return;
        try { var value = JsonNode.Parse(JsonText)!.AsObject(); if (string.IsNullOrEmpty(input)) value.Remove(key); else value[key] = numeric ? JsonValue.Create(int.Parse(input)) : JsonValue.Create(input); JsonText = value.ToJsonString(Json); Notify(); }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException) { Error = ex.Message; Notify(); }
    }
    public bool Boolean(string key) => bool.TryParse(Field(key), out bool result) ? result : key == "Visibility3DCheckLineOfSight";
    public int PointField(string key, int axis)
    {
        try
        {
        var value = Form()?[key];
        if (value is JsonObject point) return point[new[] { "x", "y", "z" }[axis]]?.GetValue<int>() ?? 0;
        if (value is JsonArray array) return axis < array.Count ? array[axis]?.GetValue<int>() ?? 0 : 0;
        if (value is JsonValue text) { var numbers = System.Text.RegularExpressions.Regex.Matches(text.ToString(), @"-?\d+"); return numbers.Count > axis ? int.Parse(numbers[axis].Value) : 0; }
        return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { return 0; }
    }
    public void SetPointField(string key, int axis, string? input)
    {
        if (!OwnsSelection || !int.TryParse(input, out int number)) return;
        try { int[] point = [PointField(key, 0), PointField(key, 1), PointField(key, 2)]; point[axis] = number; var value = JsonNode.Parse(JsonText)!.AsObject(); value[key] = new JsonObject { ["x"] = point[0], ["y"] = point[1], ["z"] = point[2] }; JsonText = value.ToJsonString(Json); Notify(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { Error = ex.Message; Notify(); }
    }
    public void SetBoolean(string key, bool input) { if (!OwnsSelection) return; var value = Form(); if (value is null) { Error = "Correct the advanced JSON before editing fields."; Notify(); return; } value[key] = input; JsonText = value.ToJsonString(Json); Notify(); }
    private void SetAreas(IEnumerable<RegionVolume> areas)
    {
        var value = Form(); if (value is null) { Error = "Correct the advanced JSON before editing volumes."; Notify(); return; } Area = areas.ToArray(); value["Area"] = JsonSerializer.SerializeToNode(Area.Select(volume => new { x1 = volume.X1, y1 = volume.Y1, z1 = volume.Z1, x2 = volume.X2, y2 = volume.Y2, z2 = volume.Z2 })); JsonText = value.ToJsonString(Json); Notify();
    }
    public void AddVolume() { if (OwnsSelection && Area.Count < 512) SetAreas(Area.Append(new(0, 0, -128, 10, 10, 128))); }
    public void RemoveVolume(int index) { if (OwnsSelection) SetAreas(Area.Where((volume, i) => i != index)); }
    public void EditVolume(int index, int axis, string? input)
    {
        if (!OwnsSelection || !int.TryParse(input, out int number)) return;
        var area = Area.ToArray(); var v = area[index]; area[index] = axis switch { 0 => v with { X1 = number }, 1 => v with { Y1 = number }, 2 => v with { Z1 = number }, 3 => v with { X2 = number }, 4 => v with { Y2 = number }, _ => v with { Z2 = number } }; SetAreas(area);
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs args) { if (!session.HasSession) Reset(); Notify(); }
    private void Reset() { Catalog = null; Selected = null; Rows = []; Area = []; Exported = null; JsonText = OriginalJson = "{}"; }
    private async Task Poll()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (Catalog is null || Busy || !session.HasSession) continue;
                Guid id = Catalog.Id;
                try
                {
                    var locks = await api.GetAsync<DocumentLock[]>(Path + "/locks", lifetime.Token); LiveLocksKnown = true;
                    if (Catalog?.Id != id) continue;
                    DocumentOwner? Owner(int record) { var held = locks.FirstOrDefault(held => held.Id == record); return held is null ? null : new(held.UserName, held.Own, held.ExpiresAt); }
                    Rows = Rows.Select(row => row with { Lock = Owner(row.Id) }).ToArray();
                    if (Selected is not null) Selected = Selected with { Summary = Selected.Summary with { Lock = Owner(Selected.Summary.Id) } }; Notify();
                }
                catch (HttpRequestException ex) { LiveLocksKnown = false; Error = "Live region locks unavailable: " + ex.Message; Notify(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { session.PropertyChanged -= SessionChanged; lifetime.Cancel(); if (polling is not null) await polling; lifetime.Dispose(); }
}
