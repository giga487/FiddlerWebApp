using Fiddler.Formats;
using Fiddler.Formats.Gumps;
using Fiddler.Graphics.Gumps;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using FiddlerWebApp.Services.Users;

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
    private Task? leaseUpdates;
    private GumpDocument? Document => Workspace.Gumps;
    private bool TargetExists => TryId(Target, out int id) && Document?.Entries.Any(entry => entry.Id == id) == true;
    private bool TargetModified => TryId(Target, out int id) && Document?.IsModified(id) == true;
    private bool OwnsTarget => TryId(Target, out int id) && Workspace.OwnsEdit(id);
    private EditLease? TargetLease => TryId(Target, out int id) ? Workspace.EditLeases().FirstOrDefault(l => l.Asset.Id == id) : null;
    private IEnumerable<GumpComparison> Filtered => Rows.Where(row => !OnlyDifferences || row.Difference != GumpDifference.Equal);
    private int PageCount => Math.Max(1, (Filtered.Count() + PageSize - 1) / PageSize);
    private IEnumerable<GumpComparison> PageRows => Filtered.Skip(Page * PageSize).Take(PageSize);

    protected override void OnInitialized()
    {
        Target = (RequestedId ?? Document?.Entries.FirstOrDefault()?.Id ?? 0).ToString();
        if (!string.IsNullOrWhiteSpace(Settings.Directory)) OutputParent = Path.Combine(Settings.Directory, ApplicationInfo.ExportDirectoryName);
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
    private int TargetId() => TryId(Target, out int id) ? id : throw new ArgumentException("Enter an ID from 0 to 0x11FFF in decimal or hexadecimal.");
    protected override void OnAfterRender(bool firstRender) { if (firstRender) leaseUpdates = UpdateLeases(); }
    private async Task UpdateLeases()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await InvokeAsync(StateHasChanged); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private void StartEditing()
    {
        if (Busy) return;
        try { Workspace.BeginEdit(TargetId()); Error = null; Info = "ID reserved for this editing session."; ShowSelection(); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void ReleaseTarget()
    {
        if (Busy) return;
        try { Workspace.ReleaseEdit(TargetId()); Error = null; Info = "ID released."; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void RefreshShared()
    {
        if (Busy) return;
        try { Workspace.RefreshShared(); Rows = []; ShowSelection(); Error = null; Info = "Shared version refreshed. Your private drafts are preserved."; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException;
    private string Image(GumpRecord record) => "data:image/png;base64," + Convert.ToBase64String(Renderer.RenderPng(record.Decode()));

    private void OpenPrimary()
    {
        if (Busy) return;
        try
        {
            Workspace.Open(Settings.Directory);
            if (Document is null) throw new InvalidDataException("The folder contains no Gumps.");
            Error = null; Info = null; Rows = []; Target = (RequestedId ?? Document.Entries.FirstOrDefault()?.Id ?? 0).ToString();
            ShowSelection();
        }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void ShowSelection()
    {
        CurrentImage = CurrentDetail = null;
        if (!Session.IsSignedIn) { CurrentDetail = "Sign in to view this workspace."; return; }
        if (Document is null || !TryId(Target, out int id)) { CurrentDetail = "Invalid target ID."; return; }
        if (!Document.Entries.Any(entry => entry.Id == id)) return;
        try { CurrentImage = Image(Document.ReadRecord(id)); }
        catch (Exception ex) when (Expected(ex)) { CurrentDetail = ex.Message; }
    }
    private async Task LoadPng(InputFileChangeEventArgs args)
    {
        if (Busy) return;
        ClearPending(); Error = null;
        Busy = true; OperationLabel = "Reading PNG";
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
        try { int id = TargetId(); Workspace.PutGump(id, Pending); Changed($"PNG added to draft ID {id}."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void Changed(string message)
    {
        Rows = []; Page = 0; ExportedDirectory = null; Error = null;
        Info = message + " Run the comparison again after making changes.";
        ShowSelection();
    }
    private void UndoTarget()
    {
        if (Busy || Document is null) return;
        try { Workspace.UndoGump(TargetId()); Changed("Changes to this ID undone."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private void DiscardAll()
    {
        if (Busy || Document is null) return;
        try { Workspace.DiscardGumps(); Changed("Drafts discarded and edit leases released."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }

    private async Task Compare()
    {
        if (Busy || Document is null) return;
        Begin("Comparing");
        try
        {
            Session.RequireUser();
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
            Info = $"Comparison complete: {Rows.Count:N0} IDs checked.";
        }
        catch (OperationCanceledException) { Info = "Comparison canceled."; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
        finally { End(); }
    }
    private void SelectComparison(int id)
    {
        Target = id.ToString(); SelectedReference = id; ReferenceImage = ReferenceDetail = null;
        ShowSelection();
        if (reference?.Entries.Any(entry => entry.Id == id) != true) { ReferenceDetail = "ID not found in the reference."; return; }
        try { ReferenceImage = Image(reference.ReadRecord(id)); }
        catch (Exception ex) when (Expected(ex)) { ReferenceDetail = ex.Message; }
    }
    private void CopyReference()
    {
        if (Busy || reference is null || SelectedReference is not int id || Document is null) return;
        try { int target = TargetId(); Workspace.PutGump(target, reference.ReadRecord(id)); Changed($"Reference Gump {id} copied to draft ID {target}."); }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
    }
    private async Task Export()
    {
        if (Busy || Document is null) return;
        Begin("Saving a new version");
        try
        {
            var token = operation!.Token;
            var task = Workspace.SaveSharedAsync(OutputParent.Trim(), token);
            running = task; var saved = await task; ExportedDirectory = saved.Directory;
            Rows = []; ShowSelection();
            Info = $"Shared revision {saved.Revision} published. Other users' published changes are included; original files were not changed.";
        }
        catch (OperationCanceledException) { Info = "Save canceled."; }
        catch (Exception ex) when (Expected(ex)) { Error = ex.Message; }
        finally { End(); }
    }
    private void OpenExport()
    {
        if (Busy || ExportedDirectory is null) return;
        try { Workspace.RefreshShared(); Rows = []; ShowSelection(); Info = "Latest shared version opened in the workspace."; Error = null; }
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
        GumpDifference.Equal => "Equal", GumpDifference.Different => "Different", GumpDifference.CurrentOnly => "Current only",
        GumpDifference.ReferenceOnly => "Reference only", _ => "Unreadable"
    };
    public async ValueTask DisposeAsync()
    {
        disposed = true; lifetime.Cancel(); operation?.Cancel();
        if (running is { } task) { try { await task; } catch (OperationCanceledException) { } catch (Exception ex) when (Expected(ex)) { } }
        if (leaseUpdates is not null) await leaseUpdates;
        reference?.Dispose(); lifetime.Dispose();
    }
}
