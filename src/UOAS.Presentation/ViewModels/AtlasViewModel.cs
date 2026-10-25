using System.Globalization;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed record AtlasImage(string Key, int Scale, int X, int Y, byte[] Png);
public sealed class AtlasViewModel(IAssetApi api) : ObservableViewModel, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? viewRequest;
    private readonly Dictionary<string, AtlasImage> cache = [];
    private Task pending = Task.CompletedTask;
    private bool disposed;
    public IReadOnlyList<AtlasMap> Maps { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public IReadOnlyDictionary<string, byte[]> Thumbnails { get; private set; } = new Dictionary<string, byte[]>();
    public AtlasMap? Current { get; private set; }
    public IReadOnlyList<AtlasImage> Images { get; private set; } = [];
    public IReadOnlyList<RegionOverlay> Overlays { get; private set; } = [];
    public Guid? RegionCatalog { get; set; }
    public bool Loading { get; private set; }
    public bool LoadingMaps { get; private set; }
    public string? Error { get; private set; }
    public string? OverlayNotice { get; private set; }
    public double Width { get; private set; } = 900;
    public double Height { get; private set; } = 480;
    public double CenterX { get; private set; }
    public double CenterY { get; private set; }
    public double Scale { get; private set; } = 1;
    public double Left => CenterX - Width * Scale / 2;
    public double Top => CenterY - Height * Scale / 2;
    public int? Slice { get; set; }
    public string SelectedKey { get; set; } = "";
    public static string Key(AtlasMap map) => map.Library + ":" + map.FileId;
    private static string Root(AtlasMap map) => $"api/atlas/{Uri.EscapeDataString(map.Library)}/{map.FileId}";
    public async Task LoadMapsAsync()
    {
        if (LoadingMaps) return; LoadingMaps = true; Error = null; ChangedAll();
        try
        {
            string previous = Current is null ? "" : Key(Current); var catalog = await api.GetAsync<AtlasCatalog>("api/atlas/maps", lifetime.Token);
            Maps = catalog.Maps; Warnings = catalog.Warnings;
            SetMap(Maps.FirstOrDefault(map => Key(map) == previous && map.Ready) ?? Maps.FirstOrDefault(map => map.Ready));
            var thumbs = new Dictionary<string, byte[]>();
            foreach (var map in Maps.Where(map => map.Ready).Take(24))
            {
                try { thumbs[Key(map)] = await api.ImageAsync(Root(map) + "/thumbnail?revision=" + map.Revision, lifetime.Token); }
                catch (HttpRequestException ex) { Error = ex.Message; }
                Thumbnails = new Dictionary<string, byte[]>(thumbs); ChangedAll();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { Error = ex.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { LoadingMaps = false; ChangedAll(); }
    }
    private void SetMap(AtlasMap? map)
    {
        Current = map; SelectedKey = map is null ? "" : Key(map); Images = []; Overlays = []; cache.Clear(); Fit();
    }
    public Task ChangeMapAsync()
    { SetMap(Maps.FirstOrDefault(map => Key(map) == SelectedKey && map.Ready)); return Task.CompletedTask; }
    public void Choose(AtlasMap map) => SetMap(map.Ready ? map : null);
    public void Focus(string mapName, IReadOnlyList<RegionVolume> area)
    {
        var map = Current?.Name == mapName ? Current : Maps.FirstOrDefault(map => map.Name == mapName && map.Ready);
        if (map is null) return;
        if (Current != map) SetMap(map);
        if (area.Count == 0) return;
        double x1 = area.Min(v => v.X1), y1 = area.Min(v => v.Y1), x2 = area.Max(v => v.X2), y2 = area.Max(v => v.Y2);
        CenterX = Math.Clamp((x1 + x2) / 2, 0, map.Width); CenterY = Math.Clamp((y1 + y2) / 2, 0, map.Height);
        Scale = Math.Clamp(Math.Max((x2 - x1) / Math.Max(1, Width - 80), (y2 - y1) / Math.Max(1, Height - 80)) * 1.2, 1, 64); Schedule();
    }
    public void Size(double width, double height)
    { if (Math.Abs(Width - width) < 1 && Math.Abs(Height - height) < 1) return; Width = Math.Max(100, width); Height = Math.Max(100, height); if (Images.Count == 0) Fit(); else Schedule(); }
    public void Fit()
    {
        if (Current is { } map) { CenterX = map.Width / 2d; CenterY = map.Height / 2d; Scale = Math.Clamp(Math.Max(map.Width / Width, map.Height / Height) * 1.04, 1, 64); }
        Schedule();
    }
    public void Zoom(double factor, double? anchorX = null, double? anchorY = null)
    {
        if (Current is null) return;
        double px = anchorX ?? Width / 2, py = anchorY ?? Height / 2, x = Left + px * Scale, y = Top + py * Scale;
        Scale = Math.Clamp(Scale * factor, 1, 64); CenterX = x + (Width / 2 - px) * Scale; CenterY = y + (Height / 2 - py) * Scale; Clamp(); Schedule();
    }
    public void Pan(double dx, double dy) { CenterX -= dx * Scale; CenterY -= dy * Scale; Clamp(); Schedule(); }
    private void Clamp() { if (Current is { } map) { CenterX = Math.Clamp(CenterX, 0, map.Width); CenterY = Math.Clamp(CenterY, 0, map.Height); } }
    public void Schedule()
    {
        if (disposed) return;
        Loading = Current is not null;
        viewRequest?.Cancel(); viewRequest?.Dispose(); viewRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pending = Update(viewRequest.Token); ChangedAll();
    }
    public Task SettleAsync() => pending;
    private async Task Update(CancellationToken token)
    {
        try
        {
            await Task.Delay(160, token); var map = Current;
            if (map is null) { Images = []; Overlays = []; return; }
            Loading = true; Error = null; ChangedAll();
            int scale = 1; while (scale < 64 && scale * 2 <= Scale) scale *= 2;
            double left = Math.Max(0, Left), top = Math.Max(0, Top), right = Math.Min(map.Width, Left + Width * Scale), bottom = Math.Min(map.Height, Top + Height * Scale);
            if (right <= left || bottom <= top) return;
            int TileCount(int s) => ((int)Math.Ceiling(right / (256 * s)) - (int)(left / (256 * s))) * ((int)Math.Ceiling(bottom / (256 * s)) - (int)(top / (256 * s)));
            while (scale < 64 && TileCount(scale) > 64) scale *= 2;
            var visible = new List<AtlasImage>();
            for (int y = (int)(top / (256 * scale)); y < Math.Ceiling(bottom / (256 * scale)); y++)
                for (int x = (int)(left / (256 * scale)); x < Math.Ceiling(right / (256 * scale)); x++)
                {
                    string key = map.Revision + $"/{scale}/{x}/{y}";
                    if (!cache.TryGetValue(key, out var tile))
                    {
                        var png = await api.ImageAsync(Root(map) + $"/tiles/{scale}/{x}/{y}?revision={map.Revision}", token);
                        token.ThrowIfCancellationRequested(); tile = new(key, scale, x, y, png); cache[key] = tile;
                    }
                    visible.Add(tile); Images = visible.ToArray(); ChangedAll();
                }
            foreach (var key in cache.Keys.Where(key => visible.All(tile => tile.Key != key)).Take(Math.Max(0, cache.Count - 128)).ToArray()) cache.Remove(key);
            await LoadOverlay(map, left, top, right - left, bottom - top, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { if (!token.IsCancellationRequested) Error = ex.Message; }
        finally { if (!token.IsCancellationRequested) { Loading = false; ChangedAll(); } }
    }
    private async Task LoadOverlay(AtlasMap map, double x, double y, double width, double height, CancellationToken token)
    {
        if (RegionCatalog is not { } catalog) { Overlays = []; return; }
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var items = new List<RegionOverlay>(); OverlayNotice = null; int total = 0, volumes = 0;
        for (int offset = 0; offset < 1000; offset += 100)
        {
            var page = await api.GetAsync<RegionOverlayPage>($"api/regions/{catalog}/overlay?map={Uri.EscapeDataString(map.Name)}&x={N(x)}&y={N(y)}&width={N(width)}&height={N(height)}&offset={offset}&limit=100" + (Slice is int z ? $"&z={z}" : ""), token);
            total = page.Total;
            foreach (var row in page.Items) { if (volumes + row.Area.Count > 4000) break; items.Add(row); volumes += row.Area.Count; }
            if (offset + page.Items.Count >= total || items.Count < offset + page.Items.Count) break;
        }
        token.ThrowIfCancellationRequested(); Overlays = items;
        if (items.Count < total) OverlayNotice = $"Showing {items.Count} of {total} regions in view. Zoom in to reduce overlapping volumes.";
    }
    public void UpdateOwners(IReadOnlyList<DocumentLock> owners)
    {
        Overlays = Overlays.Select(row => { var held = owners.FirstOrDefault(owner => owner.Id == row.Summary.Id); return row with { Summary = row.Summary with { Lock = held is null ? null : new(held.UserName, held.Own, held.ExpiresAt) } }; }).ToArray(); ChangedAll();
    }
    public void ClearOverlay() { RegionCatalog = null; Overlays = []; ChangedAll(); }
    public async ValueTask DisposeAsync()
    { if (disposed) return; disposed = true; lifetime.Cancel(); viewRequest?.Cancel(); await pending; viewRequest?.Dispose(); lifetime.Dispose(); cache.Clear(); }
}
