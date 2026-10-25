using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed record CompositionPiece(int Key, int AssetId, byte[] Png, int Width, int Height, float X, float Y);

public sealed class CompositionViewModel : ObservableViewModel
{
    private readonly IAssetApi api;
    private readonly WorkspaceViewModel workspace;
    private int nextKey;
    public CompositionViewModel(IAssetApi api, WorkspaceViewModel workspace)
    {
        this.api = api; this.workspace = workspace;
        AddCommand = new(Add, () => !Busy && workspace.Workspace is not null && Pieces.Count < 128);
    }
    public AsyncCommand AddCommand { get; }
    public int AssetId { get; set; }
    public int BoardWidth { get; set; } = 1024;
    public int BoardHeight { get; set; } = 768;
    public int? SelectedKey { get; private set; }
    public IReadOnlyList<CompositionPiece> Pieces { get; private set; } = [];
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    private async Task Add()
    {
        Busy = true; Error = null; ChangedAll();
        try
        {
            var asset = await api.GetAsync<AssetDetails>($"{workspace.WorkspacePath}/assets/Gump/{AssetId}");
            if (asset.Empty) throw new InvalidOperationException("This Gump ID is empty.");
            if (Pieces.Sum(piece => (long)piece.Width * piece.Height) + (long)asset.Width * asset.Height > 16_000_000) throw new InvalidOperationException("The composition exceeds 16 million pixels.");
            var png = await api.ImageAsync($"{workspace.WorkspacePath}/assets/Gump/{AssetId}/image");
            int key = ++nextKey;
            Pieces = [.. Pieces, new(key, AssetId, png, asset.Width, asset.Height, Pieces.Count % 6 * 128, Pieces.Count / 6 * 128)];
            SelectedKey = key;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { Error = ex.Message; }
        finally { Busy = false; ChangedAll(); AddCommand.Notify(); }
    }
    public void Select(int key) { SelectedKey = key; ChangedAll(); }
    public void Move(int key, float x, float y)
    { Pieces = Pieces.Select(piece => piece.Key == key ? piece with { X = Math.Clamp(x, 0, 2048), Y = Math.Clamp(y, 0, 2048) } : piece).ToArray(); ChangedAll(); }
    public void RemoveSelected() { Pieces = Pieces.Where(piece => piece.Key != SelectedKey).ToArray(); SelectedKey = null; ChangedAll(); }
    public void Clear() { Pieces = []; SelectedKey = null; ChangedAll(); }
}
