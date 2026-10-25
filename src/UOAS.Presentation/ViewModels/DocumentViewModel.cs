using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed class DocumentViewModel : ObservableViewModel, IAsyncDisposable
{
    private readonly IAssetApi api;
    private readonly WorkspaceViewModel workspace;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<AsyncCommand> commands = [];
    private Guid? workspaceId;
    private Task? loop;
    public DocumentViewModel(IAssetApi api, WorkspaceViewModel workspace)
    {
        this.api = api; this.workspace = workspace; workspaceId = workspace.Workspace?.Id; workspace.PropertyChanged += WorkspaceChanged;
        FilesCommand = Command(Files, () => workspace.Workspace is not null);
        OpenCommand = Command(Open, () => AvailableFiles.Count > 0);
        FindCommand = Command(async () => { Page = 0; await List(); await Select(RecordId); }, () => Document is not null);
        SearchCommand = Command(async () => { Page = 0; await List(); }, () => Document is not null);
        RefreshCommand = Command(async () => { Document = await api.PostAsync<DocumentInfo>(Path + "/refresh"); await List(); if (Selected is not null) await Select(Selected.Id); }, () => Document is not null);
        AcquireCommand = Command(async () => { int id = Selected!.Id; await api.PostAsync<DocumentOwner>($"{Path}/locks/{id}"); await Select(id); }, () => LiveLocksKnown && Selected is { Lock: null });
        SaveCommand = Command(Save, () => OwnsSelection);
        DiscardCommand = Command(async () => { int id = Selected!.Id; await api.DeleteAsync($"{Path}/locks/{id}?discard=true"); await Select(id); await List(); }, () => Selected is not null && (Selected.Lock?.Own == true || Selected.Draft));
        ReleaseCommand = Command(async () => { int id = Selected!.Id; await api.DeleteAsync($"{Path}/locks/{id}"); Selected = Selected with { Lock = null }; }, () => OwnsSelection && Selected?.Draft != true);
        PublishCommand = Command(Publish, () => Document?.DraftCount > 0);
        NextCommand = Command(async () => { Page++; await List(); }, () => Document is not null && Page + 1 < PageCount);
        PreviousCommand = Command(async () => { Page--; await List(); }, () => Document is not null && Page > 0);
    }
    private AsyncCommand Command(Func<Task> action, Func<bool> allowed)
    { var command = new AsyncCommand(() => Run(action), () => !Busy && allowed()); commands.Add(command); return command; }
    public AsyncCommand FilesCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand FindCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AcquireCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public AsyncCommand ReleaseCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand NextCommand { get; }
    public AsyncCommand PreviousCommand { get; }
    public IReadOnlyList<DocumentFile> AvailableFiles { get; private set; } = [];
    public int FileIndex { get; set; }
    public DocumentInfo? Document { get; private set; }
    public IReadOnlyList<DocumentRecord> Records { get; private set; } = [];
    public DocumentRecord? Selected { get; private set; }
    public string JsonText { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public int Flag { get; set; }
    public string Flags { get; set; } = "0";
    public int RecordId { get; set; }
    public int FontIndex { get; set; }
    public string Character { get; set; } = "A";
    public string PreviewText { get; set; } = "Ultima Online";
    public byte[]? TextPreview { get; private set; }
    public string Search { get; set; } = "";
    public string Shadow { get; set; } = "#000000";
    public string Middle { get; set; } = "#808080";
    public string Highlight { get; set; } = "#FFFFFF";
    public int CopyHueId { get; set; } = 1;
    private readonly Stack<string> hueHistory = new();
    public int Page { get; private set; }
    public int PageCount { get; private set; } = 1;
    public byte[]? Preview { get; private set; }
    public bool Busy { get; private set; }
    public bool LiveLocksKnown { get; private set; } = true;
    public bool OwnsSelection => LiveLocksKnown && Selected?.Lock?.Own == true;
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string Path => $"{workspace.WorkspacePath}/documents/{Document!.Id}";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private void Notify() { ChangedAll(); foreach (var command in commands) command.Notify(); }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (workspace.Workspace?.Id == workspaceId) return;
        workspaceId = workspace.Workspace?.Id; Document = null; Selected = null; Preview = TextPreview = null; AvailableFiles = []; Records = []; hueHistory.Clear(); Notify();
    }
    private async Task Run(Func<Task> action)
    {
        if (Busy) return; Busy = true; Error = null; Notify();
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or JsonException or FormatException or OverflowException) { Error = ex.Message; }
        finally { Busy = false; Notify(); }
    }
    private async Task Files()
    { AvailableFiles = await api.GetAsync<DocumentFile[]>(workspace.WorkspacePath + "/documents/files", lifetime.Token); FileIndex = 0; }
    private async Task Open()
    {
        if (FileIndex < 0 || FileIndex >= AvailableFiles.Count) throw new ArgumentException("Select a document file.");
        var file = AvailableFiles[FileIndex];
        Document = await api.PostAsync<DocumentInfo>(workspace.WorkspacePath + "/documents", new OpenDocumentRequest(file.Kind, file.FileName), lifetime.Token);
        Selected = null; Preview = TextPreview = null; Search = ""; FontIndex = 0; Page = 0; await List();
        loop ??= Poll();
    }
    private async Task List()
    {
        var page = await api.GetAsync<DocumentPage>($"{Path}/records?offset={Page * 24}&limit=24&search={Uri.EscapeDataString(Search)}", lifetime.Token);
        Records = page.Items; PageCount = Math.Max(1, (page.Total + 23) / 24);
        Document = await api.GetAsync<DocumentInfo>(Path, lifetime.Token);
    }
    public Task SelectAsync(int id) => Run(() => Select(id));
    public Task FindGlyphAsync() => Run(async () => { if (Character.Length == 0) throw new ArgumentException("Enter a character."); await Select(checked(FontIndex * 65536 + Character[0])); });
    public Task PreviewTextAsync() => Run(async () => TextPreview = await api.ImageAsync($"{Path}/text?font={FontIndex}&text={Uri.EscapeDataString(PreviewText)}"));
    public Task CloseAsync() => Run(async () => { if (Document is null) return; await api.DeleteAsync(Path); Document = null; Selected = null; Records = []; Preview = null; });
    private async Task Select(int id)
    {
        Selected = await api.GetAsync<DocumentRecord>($"{Path}/records/{id}", lifetime.Token);
        RecordId = id; JsonText = JsonSerializer.Serialize(Selected.Value, Json); Preview = null;
        hueHistory.Clear();
        Name = Selected.Value.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
        Text = Selected.Value.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        Flag = Selected.Value.TryGetProperty("flag", out var flag) ? flag.GetByte() : 0;
        Flags = Selected.Value.TryGetProperty("flags", out var flags) ? "0x" + flags.GetUInt64().ToString("X") : "0";
        if (Selected.Value.TryGetProperty("imagePng", out var png) && png.ValueKind == JsonValueKind.String) Preview = Convert.FromBase64String(png.GetString()!);
    }
    private async Task Save()
    {
        var value = JsonNode.Parse(JsonText)?.AsObject() ?? throw new ArgumentException("Provide a JSON record.");
        if (Document!.Kind == DocumentKind.Cliloc) { value["text"] = Text; value["flag"] = Flag; }
        if (Document.Kind is DocumentKind.Hue or DocumentKind.TileDataItem or DocumentKind.TileDataLand) value["name"] = Name;
        if (Document.Kind is DocumentKind.TileDataItem or DocumentKind.TileDataLand)
            value["flags"] = Flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(Flags[2..], NumberStyles.HexNumber) : ulong.Parse(Flags);
        await api.PutAsync($"{Path}/records/{Selected!.Id}", value, lifetime.Token);
        await Select(Selected.Id); await List();
    }
    public Task UploadGlyphAsync(byte[] png) => Run(async () =>
    {
        if (!OwnsSelection || Document?.Kind != DocumentKind.Font) throw new InvalidOperationException("Acquire this glyph before importing an image.");
        var value = JsonNode.Parse(JsonText)!.AsObject(); value["imagePng"] = Convert.ToBase64String(png);
        await api.PutAsync($"{Path}/records/{Selected!.Id}", value, lifetime.Token); await Select(Selected.Id); await List();
    });
    public IReadOnlyList<string> HueColors()
    {
        if (Selected is null || !Selected.Value.TryGetProperty("colors", out var colors)) return [];
        return colors.EnumerateArray().Select(color => { int value = color.GetInt32(); return $"#{((value >> 10) & 31) * 255 / 31:X2}{((value >> 5) & 31) * 255 / 31:X2}{(value & 31) * 255 / 31:X2}"; }).ToArray();
    }
    public void SetHueColor(int index, string color)
    {
        if (!OwnsSelection || color.Length != 7) return;
        var value = JsonNode.Parse(JsonText)!.AsObject(); var colors = value["colors"]!.AsArray();
        hueHistory.Push(JsonText); colors[index] = PaletteMath.Parse(color);
        JsonText = value.ToJsonString(Json); Selected = Selected! with { Value = JsonSerializer.SerializeToElement(value) }; Notify();
    }
    public IReadOnlyList<ushort> Palette => Selected is not null && Selected.Value.TryGetProperty("colors", out var colors) ? colors.EnumerateArray().Select(color => color.GetUInt16()).ToArray() : [];
    private void SetPalette(IEnumerable<ushort> colors)
    {
        if (!OwnsSelection || Document?.Kind != DocumentKind.Hue) return;
        var value = JsonNode.Parse(JsonText)!.AsObject(); hueHistory.Push(JsonText);
        value["colors"] = JsonSerializer.SerializeToNode(colors); JsonText = value.ToJsonString(Json); Selected = Selected! with { Value = JsonSerializer.SerializeToElement(value) }; Notify();
    }
    public void GenerateHue() => SetPalette(PaletteMath.Gradient(PaletteMath.Parse(Shadow), PaletteMath.Parse(Middle), PaletteMath.Parse(Highlight)));
    public void UndoHue() { if (!OwnsSelection || !hueHistory.TryPop(out var json)) return; JsonText = json; Selected = Selected! with { Value = JsonSerializer.Deserialize<JsonElement>(json) }; Notify(); }
    public Task CopyHueAsync() => Run(async () => { var source = await api.GetAsync<DocumentRecord>($"{Path}/records/{CopyHueId}"); SetPalette(source.Value.GetProperty("colors").EnumerateArray().Select(color => color.GetUInt16())); });
    public int NumericField(string key) => JsonNode.Parse(JsonText)?[key]?.GetValue<int>() ?? 0;
    public void SetNumericField(string key, string? input)
    {
        if (!OwnsSelection || !int.TryParse(input, out int number)) return;
        var value = JsonNode.Parse(JsonText)!.AsObject(); value[key] = number; JsonText = value.ToJsonString(Json); Notify();
    }
    public bool HasFlag(ulong mask) { try { return (ParseFlags() & mask) != 0; } catch (Exception ex) when (ex is FormatException or OverflowException) { return false; } }
    private ulong ParseFlags() => Flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(Flags[2..], NumberStyles.HexNumber) : ulong.Parse(Flags);
    public void ToggleFlag(ulong mask, bool enabled) { if (OwnsSelection) Flags = "0x" + (enabled ? ParseFlags() | mask : ParseFlags() & ~mask).ToString("X"); Notify(); }
    private async Task Publish()
    {
        var result = await api.PostAsync<ExportResult>(Path + "/publish", token: lifetime.Token);
        await List(); if (Selected is not null) await Select(Selected.Id);
        Notice = $"Published document revision {result.Revision} to {result.Directory}.";
    }
    private async Task Poll()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (Document is null || Busy || !workspace.HasSession) continue;
                Guid requested = Document.Id;
                DocumentLock[] owners;
                try { owners = await api.GetAsync<DocumentLock[]>(Path + "/locks", lifetime.Token); LiveLocksKnown = true; }
                catch (HttpRequestException ex) { LiveLocksKnown = false; Error = "Live lock status unavailable: " + ex.Message; Notify(); continue; }
                if (Document?.Id != requested) continue;
                DocumentOwner? Owner(int id) { var owner = owners.FirstOrDefault(lease => lease.Id == id); return owner is null ? null : new(owner.UserName, owner.Own, owner.ExpiresAt); }
                Records = Records.Select(record => record with { Lock = Owner(record.Id) }).ToArray();
                if (Selected is not null) Selected = Selected with { Lock = Owner(Selected.Id) };
                Notify();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (HttpRequestException ex) { Error = ex.Message; Notify(); }
    }
    public async ValueTask DisposeAsync() { workspace.PropertyChanged -= WorkspaceChanged; lifetime.Cancel(); if (loop is not null) await loop; lifetime.Dispose(); }
}
