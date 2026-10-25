using System.ComponentModel;
using UOAS.Contracts;
using UOAS.Presentation.Services;

namespace UOAS.Presentation.ViewModels;

public sealed record ColorSample(ResourceKind Kind, int Id, byte[] Png, bool PartialHue);
public sealed record HueModeInfo(bool PartialHue);
public sealed class ColorBoardViewModel : ObservableViewModel, IDisposable
{
    private readonly WorkspaceViewModel workspace;
    private readonly IAssetApi api;
    private Guid? workspaceId;
    public ColorBoardViewModel(IAssetApi api, WorkspaceViewModel workspace)
    {
        this.api = api; this.workspace = workspace; workspaceId = workspace.Workspace?.Id; workspace.PropertyChanged += Updated;
        AddCommand = new(Add, () => !Busy && workspace.Workspace is not null && Samples.Count < 8);
    }
    public AsyncCommand AddCommand { get; }
    public ResourceKind Kind { get; set; } = ResourceKind.Item;
    public int Id { get; set; }
    public string Mode { get; set; } = "Automatic";
    public string Background { get; set; } = "Checkerboard";
    public int Zoom { get; set; } = 2;
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<ColorSample> Samples { get; private set; } = [];
    private async Task Add()
    {
        Busy = true; Error = null; Notify();
        try
        {
            if (Samples.Any(sample => sample.Kind == Kind && sample.Id == Id)) return;
            var png = await api.ImageAsync($"{workspace.WorkspacePath}/assets/{Kind}/{Id}/image");
            var mode = await api.GetAsync<HueModeInfo>($"{workspace.WorkspacePath}/tools/hue-mode/{Kind}/{Id}");
            Samples = [..Samples, new(Kind, Id, png, mode.PartialHue)];
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { Error = ex.Message; }
        finally { Busy = false; Notify(); }
    }
    public void Remove(ColorSample sample) { Samples = Samples.Where(item => item != sample).ToArray(); Notify(); }
    public void Clear() { Samples = []; Notify(); }
    private void Notify() { ChangedAll(); AddCommand.Notify(); }
    private void Updated(object? sender, PropertyChangedEventArgs args) { if (workspaceId == workspace.Workspace?.Id) return; workspaceId = workspace.Workspace?.Id; Clear(); }
    public void Dispose() => workspace.PropertyChanged -= Updated;
}
