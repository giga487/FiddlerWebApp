using System.Globalization;
using Fiddler.Formats;
using Fiddler.Formats.Hues;
using FiddlerWebApp.Services;
using Microsoft.AspNetCore.Components;

namespace FiddlerWebApp.Components.Pages;

public partial class ColorStudio
{
    [SupplyParameterFromQuery(Name = "asset")] public string? RequestedKind { get; set; }
    [SupplyParameterFromQuery(Name = "id")] public string? RequestedId { get; set; }
    private const int LibraryPageSize = 32;
    private int libraryPage, selectedSlot, zoom = 1;
    private string search = "", hueName = "", sampleId = "", outputDirectory = "", background = "checker";
    private string slotColor = "#000000", shadow = "#000000", middle = "#848484", highlight = "#ffffff";
    private string? error, exportPath, lastRequest;
    private bool discardRequested, showCatalog;
    private int catalogPage, catalogFirstShown, catalogZoom = 1;
    private static readonly int[] CatalogPageSizes = [12, 24, 48, 96, 192];
    private int catalogPageSize = CatalogPageSizes[0];
    private readonly List<(int Id, string Image)> catalogTiles = [];
    private int CatalogPageCount => Math.Max(1, (AssetsWorkspace.Entries(sampleKind).Count + catalogPageSize - 1) / catalogPageSize);
    private AssetKind sampleKind = AssetKind.Item;
    private string sampleMode = "Auto";
    private ushort[] colors = new ushort[32];
    private readonly Dictionary<int, string> Originals = [], Previews = [];
    private HueEntry? Current => Studio.Document is { } document && Studio.SelectedId > 0 ? document.Read(Studio.SelectedId) : null;
    private IReadOnlyList<int> MatchingIds
    {
        get
        {
            if (Studio.Document is not { } document) return [];
            string query = search.Trim();
            bool isId = TryId(query, out int id);
            return Enumerable.Range(1, document.Count).Where(i => query.Length == 0 || (isId ? i == id : document.Read(i).Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
    }
    private int LibraryPageCount => Math.Max(1, (MatchingIds.Count + LibraryPageSize - 1) / LibraryPageSize);

    protected override void OnInitialized()
    {
        LoadSelection();
        RefreshBoard();
    }
    protected override void OnParametersSet()
    {
        string request = RequestedKind + ":" + RequestedId;
        if (request == lastRequest || RequestedId is null) return;
        lastRequest = request;
        if (Enum.TryParse<AssetKind>(RequestedKind, true, out var kind) && kind is AssetKind.Item or AssetKind.Gump)
        {
            sampleKind = kind;
            sampleId = RequestedId;
        }
    }

    private void Run(Action action)
    {
        error = null;
        exportPath = null;
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        { error = ex.Message; }
    }
    private void Open(bool discard) => Run(() =>
    {
        Studio.Open(Settings.Directory, discard);
        Originals.Clear(); Previews.Clear();
        showCatalog = false; catalogTiles.Clear(); catalogPage = 0;
        discardRequested = false; search = ""; libraryPage = 0;
        LoadSelection();
    });
    private void SelectHue(int id) => Run(() => { Studio.SelectedId = id; LoadSelection(); RefreshPreviews(); });
    private void LoadSelection()
    {
        if (Current is not { } entry) return;
        colors = entry.Colors.ToArray();
        hueName = entry.Name;
        slotColor = Hex(colors[selectedSlot]);
        shadow = Hex(colors[0]); middle = Hex(colors[15]); highlight = Hex(colors[31]);
    }
    private void NewHue() => Run(() =>
    {
        var document = Studio.Document ?? throw new InvalidOperationException("Open a library first.");
        Studio.SelectedId = document.Add("New hue", HuePalette.Gradient(0, 0x4210, 0x7FFF));
        search = ""; libraryPage = (Studio.SelectedId - 1) / LibraryPageSize;
        LoadSelection(); RefreshPreviews();
    });
    private void DuplicateHue() => Run(() =>
    {
        if (Current is not { } entry) return;
        string name = "Copy " + new string(entry.Name.Where(c => c >= 32 && c <= 126).ToArray());
        Studio.SelectedId = Studio.Document!.Add(name[..Math.Min(20, name.Length)], entry.Colors.ToArray().Select(c => (ushort)(c & 0x7FFF)).ToArray());
        search = ""; libraryPage = (Studio.SelectedId - 1) / LibraryPageSize;
        LoadSelection(); RefreshPreviews();
    });
    private void SelectSlot(int slot) { selectedSlot = slot; slotColor = Hex(colors[slot]); }
    private void ChangeSlot(ChangeEventArgs args) => Run(() =>
    {
        ushort[] next = colors.ToArray();
        next[selectedSlot] = ParseColor(args.Value?.ToString() ?? "");
        Commit(hueName, next);
    });
    private void Rename(ChangeEventArgs args) => Run(() => Commit(args.Value?.ToString() ?? "", colors));
    private void Commit(string name, ushort[] next)
    {
        if (name == hueName && next.AsSpan().SequenceEqual(colors)) return;
        Studio.Document!.Put(Studio.SelectedId, name, next);
        LoadSelection(); RefreshPreviews();
    }
    private void Generate() => Run(() => Commit(hueName, HuePalette.Gradient(ParseColor(shadow), ParseColor(middle), ParseColor(highlight))));
    private void Undo() => Run(() => { Studio.Document!.Undo(Studio.SelectedId); LoadSelection(); RefreshPreviews(); });
    private void Restore() => Run(() => { Studio.Document!.Restore(Studio.SelectedId); LoadSelection(); RefreshPreviews(); });
    private void AddSample() => Run(() =>
    {
        if (Studio.Document is null) throw new InvalidOperationException("Open a hue library first.");
        if (!TryId(sampleId.Trim(), out int id) || id < 0) throw new ArgumentException("Enter an asset ID in decimal or hexadecimal (0x…).");
        EnsureAssets();
        var entry = AssetsWorkspace.Entries(sampleKind).FirstOrDefault(e => e.Id == id)
            ?? throw new KeyNotFoundException("This asset ID is not present in the selected collection.");
        HueMode mode = sampleMode == "Auto" ? Resources.HueModeFor(Studio.DirectoryPath, sampleKind, id) : Enum.Parse<HueMode>(sampleMode);
        var sample = Studio.Add(sampleKind, id, AssetsWorkspace.Read(entry), mode);
        Originals[sample.Key] = Png(sample.Image);
        RefreshPreview(sample);
    });
    private void EnsureAssets()
    {
        if (Studio.Document is null) throw new InvalidOperationException("Open a hue library first.");
        if (!AssetsWorkspace.IsOpen || !string.Equals(AssetsWorkspace.CurrentDirectory, Studio.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            AssetsWorkspace.Open(Studio.DirectoryPath);
    }
    private void ToggleCatalog() => Run(() =>
    {
        if (showCatalog) { showCatalog = false; return; }
        EnsureAssets(); LoadCatalog(); showCatalog = true;
    });
    private void ResetCatalog() => Run(() => { catalogPage = 0; if (showCatalog) { EnsureAssets(); LoadCatalog(); } });
    private void ChangeCatalogPageSize() => Run(() =>
    {
        catalogPage = Math.Min(CatalogPageCount - 1, catalogFirstShown / catalogPageSize);
        LoadCatalog();
    });
    private void LoadCatalog()
    {
        catalogTiles.Clear();
        catalogFirstShown = catalogPage * catalogPageSize;
        foreach (var entry in AssetsWorkspace.Entries(sampleKind).Skip(catalogFirstShown).Take(catalogPageSize))
        {
            try { catalogTiles.Add((entry.Id, Png(AssetsWorkspace.Read(entry)))); }
            catch (Exception ex) when (ex is IOException or KeyNotFoundException) { /* Skip unreadable previews; ID lookup reports the record error. */ }
        }
    }
    private void PreviousAssets() => Run(() => { catalogPage = Math.Max(0, catalogPage - 1); LoadCatalog(); });
    private void NextAssets() => Run(() => { catalogPage = Math.Min(CatalogPageCount - 1, catalogPage + 1); LoadCatalog(); });
    private void AddCatalogSample(int id) { sampleId = id.ToString(CultureInfo.InvariantCulture); AddSample(); }
    private void RemoveSample(int key) { Studio.Remove(key); Originals.Remove(key); Previews.Remove(key); }
    private void ChangeMode(ColorSample sample, ChangeEventArgs args) => Run(() =>
    {
        if (!Enum.TryParse<HueMode>(args.Value?.ToString(), out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Choose a valid color mode.");
        sample.Mode = mode; RefreshPreview(sample);
    });
    private void RefreshBoard()
    {
        foreach (var sample in Studio.Samples) Originals[sample.Key] = Png(sample.Image);
        RefreshPreviews();
    }
    private void RefreshPreviews() { foreach (var sample in Studio.Samples) RefreshPreview(sample); }
    private void RefreshPreview(ColorSample sample) => Previews[sample.Key] = Current is null ? Originals[sample.Key] : Png(HuePalette.Apply(sample.Image, colors, sample.Mode));
    private string Png(DecodedImage image) => "data:image/png;base64," + Convert.ToBase64String(Renderer.RenderThumbnail(image, 192, 160));
    private void Export() => Run(() => exportPath = Studio.Document!.Export(outputDirectory));
    private void ResetLibraryPage() => libraryPage = 0;
    private void PreviousHues() => libraryPage = Math.Max(0, libraryPage - 1);
    private void NextHues() => libraryPage = Math.Min(LibraryPageCount - 1, libraryPage + 1);
    private static bool TryId(string value, out int id) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id)
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    private static string ModeName(HueMode mode) => mode == HueMode.Full ? "Full color" : "Gray pixels only";
    private static string Hex(ushort color) => "#" + (HuePalette.ToArgb(color) & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    private static ushort ParseColor(string value)
    {
        if (value.Length != 7 || value[0] != '#' || !int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            throw new ArgumentException("Choose a valid RGB color.");
        return HuePalette.FromRgb(rgb >> 16, (rgb >> 8) & 255, rgb & 255);
    }
    private static string Ramp(ushort[] palette) => "linear-gradient(to right," + string.Join(',', palette.Select(Hex)) + ")";
}
