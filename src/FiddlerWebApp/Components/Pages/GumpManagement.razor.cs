using Fiddler.Formats;
using Fiddler.Formats.Gumps;
using Fiddler.Graphics.Gumps;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace FiddlerWebApp.Components.Pages;

public partial class GumpManagement : IAsyncDisposable
{
    [SupplyParameterFromQuery(Name = "id")] public int? RequestedId { get; set; }
    private const int PageSize = 40;
    private string Target = "0", ReferencePath = "", ReferenceIndex = "", OutputParent = "";
    private string? Error, Info, PendingImage, PendingName, CurrentImage, ReferenceImage, CurrentDetail, ReferenceDetail, ReferenceName, ExportedDirectory;
    private string OperationLabel = "";
    private int Progress, UploadVersion, Page;
    private int? SelectedReference;
    private bool Busy, OnlyDifferences = true, disposed;
    private GumpRecord? Pending;
    private IGumpArchive? reference;
    private IReadOnlyList<GumpComparison> Rows = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private Task? running;
    private GumpDocument? Document => Workspace.Gumps;
    private bool TargetExists => TryId(Target, out int id) && Document?.Entries.Any(entry => entry.Id == id) == true;
    private bool TargetModified => TryId(Target, out int id) && Document?.IsModified(id) == true;
    private IEnumerable<GumpComparison> Filtered => Rows.Where(row => !OnlyDifferences || row.Difference != GumpDifference.Equal);
    private int PageCount => Math.Max(1, (Filtered.Count() + PageSize - 1) / PageSize);
    private IEnumerable<GumpComparison> PageRows => Filtered.Skip(Page * PageSize).Take(PageSize);

    protected override void OnInitialized()
    {
        Target = (RequestedId ?? Document?.Entries.FirstOrDefault()?.Id ?? 0).ToString();
        if (!string.IsNullOrWhiteSpace(Settings.Directory)) OutputParent = Path.Combine(Settings.Directory, "FiddlerExports");
        ShowSelection();
    }
    private static bool TryId(string value, out int id)
    {
        value = value.Trim();
        bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(value[2..], System.Globalization.NumberStyles.AllowHexSpecifier, null, out id)
            : int.TryParse(value, out id);
        return parsed && id >= 0 && id < GumpUopReader.MaxSlots;
    }
    private int TargetId() => TryId(Target, out int id) ? id : throw new ArgumentException("Inserisci un ID da 0 a 0x11FFF, decimale o esadecimale.");
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException;
    private string Image(GumpRecord record) => "data:image/png;base64," + Convert.ToBase64String(Renderer.RenderPng(record.Decode()));

    private void OpenPrimary()
    {
        if (Busy) return;
        try
        {
            Workspace.Open(Settings.Directory);
            if (Document is null) throw new InvalidDataException("La cartella non contiene Gump.");
            Error = null; Info = null; Rows = []; Target = (RequestedId ?? Document.Entries.FirstOrDefault()?.Id ?? 0).ToString();
            ShowSelection();
        }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void ShowSelection()
    {
        CurrentImage = CurrentDetail = null;
        if (Document is null || !TryId(Target, out int id)) { CurrentDetail = "ID di destinazione non valido."; return; }
        if (!Document.Entries.Any(entry => entry.Id == id)) return;
        try { CurrentImage = Image(Document.ReadRecord(id)); }
        catch (Exception ex) when (Expected(ex)) { CurrentDetail = ex.Message; }
    }
    private async Task LoadPng(InputFileChangeEventArgs args)
    {
        if (Busy) return;
        ClearPending(); Error = null;
        Busy = true; OperationLabel = "Lettura PNG";
        try
        {
            using var memory = new MemoryStream();
            await using var input = args.File.OpenReadStream(GumpImageImporter.MaxUploadBytes, lifetime.Token);
            await input.CopyToAsync(memory, lifetime.Token);
            Pending = GumpImageImporter.ImportPng(memory.ToArray());
            PendingName = args.File.Name;
            PendingImage = Image(Pending);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
        finally { Busy = false; UploadVersion++; }
    }
    private void ClearPending() { Pending = null; PendingImage = PendingName = null; }
    private void InsertPng()
    {
        if (Busy || Pending is null || Document is null) return;
        try { int id = TargetId(); Document.Put(id, Pending); Changed($"PNG inserito nell'ID {id} in bozza."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void Changed(string message)
    {
        Rows = []; Page = 0; ExportedDirectory = null; Error = null;
        Info = message + " Il confronto va ricalcolato dopo le modifiche.";
        ShowSelection();
    }
    private void UndoTarget()
    {
        if (Busy || Document is null) return;
        try { Document.Undo(TargetId()); Changed("Modifica dell'ID annullata."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void DiscardAll() { if (!Busy && Document is not null) { Document.Discard(); Changed("Bozze scartate."); } }

    private async Task Compare()
    {
        if (Busy || Document is null) return;
        Begin("Confronto in corso");
        try
        {
            var next = GumpArchive.Open(ReferencePath.Trim(), string.IsNullOrWhiteSpace(ReferenceIndex) ? null : ReferenceIndex.Trim());
            reference?.Dispose(); reference = next; ReferenceName = ReferencePath.Trim();
            ReferenceImage = ReferenceDetail = null; SelectedReference = null;
            Rows = [];
            var token = operation!.Token;
            var progress = new Progress<int>(value => { if (!disposed) _ = InvokeAsync(() => { if (!disposed) { Progress = value; StateHasChanged(); } }); });
            var task = Task.Run(() => GumpComparer.Compare(Document, next, token, progress), token);
            running = task;
            Rows = await task; Page = 0;
            int? selected = Rows.Where(row => row.Difference is GumpDifference.Different or GumpDifference.ReferenceOnly).Select(row => (int?)row.Id).FirstOrDefault()
                ?? Rows.Select(row => (int?)row.Id).FirstOrDefault();
            if (selected is int id) SelectComparison(id);
            Info = $"Confronto completato: {Rows.Count:N0} ID analizzati.";
        }
        catch (OperationCanceledException) { Info = "Confronto annullato."; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
        finally { End(); }
    }
    private void SelectComparison(int id)
    {
        Target = id.ToString(); SelectedReference = id; ReferenceImage = ReferenceDetail = null;
        ShowSelection();
        if (reference?.Entries.Any(entry => entry.Id == id) != true) { ReferenceDetail = "ID non presente nel riferimento."; return; }
        try { ReferenceImage = Image(reference.ReadRecord(id)); }
        catch (Exception ex) when (Expected(ex)) { ReferenceDetail = ex.Message; }
    }
    private void CopyReference()
    {
        if (Busy || reference is null || SelectedReference is not int id || Document is null) return;
        try { int target = TargetId(); Document.Put(target, reference.ReadRecord(id)); Changed($"Gump {id} del riferimento copiato nell'ID {target} in bozza."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private async Task Export()
    {
        if (Busy || Document is null) return;
        Begin("Salvataggio nuova versione");
        try
        {
            var token = operation!.Token;
            var task = Task.Run(() => Document.ExportMul(OutputParent.Trim(), token), token);
            running = task; ExportedDirectory = await task;
            Info = "Nuova coppia MUL/IDX salvata. Gli originali non sono stati modificati.";
        }
        catch (OperationCanceledException) { Info = "Salvataggio annullato."; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
        finally { End(); }
    }
    private void OpenExport()
    {
        if (Busy || ExportedDirectory is null) return;
        try { Workspace.Open(ExportedDirectory, discardChanges: true); Rows = []; ShowSelection(); Info = "Versione salvata aperta nel workspace."; Error = null; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void Begin(string label) { Busy = true; Error = Info = null; Progress = 0; OperationLabel = label; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); }
    private void End() { operation?.Dispose(); operation = null; running = null; Busy = false; }
    private void Cancel() => operation?.Cancel();
    private void ResetPage() => Page = 0;
    private void PreviousPage() => Page = Math.Max(0, Page - 1);
    private void NextPage() => Page = Math.Min(PageCount - 1, Page + 1);
    private static string DifferenceLabel(GumpDifference value) => value switch
    {
        GumpDifference.Equal => "Uguali", GumpDifference.Different => "Diversi", GumpDifference.CurrentOnly => "Solo attuale",
        GumpDifference.ReferenceOnly => "Solo riferimento", _ => "Non leggibili"
    };
    public async ValueTask DisposeAsync()
    {
        disposed = true; lifetime.Cancel(); operation?.Cancel();
        if (running is { } task) { try { await task; } catch (OperationCanceledException) { } catch (Exception ex) when (Expected(ex)) { } }
        reference?.Dispose(); lifetime.Dispose();
    }
}
