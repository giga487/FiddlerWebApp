using Fiddler.Plugin.Abstractions;

namespace Fiddler.Plugin.AssetInfo;

public sealed class AssetInfoPlugin : IFiddlerPlugin
{
    public int ApiVersion => 1;
    public string Id => "fiddler.asset-info";
    public string Name => "Asset information";
    public string Version => "1.0.0";
    public string Description => "Inspect image dimensions, transparency and distinct visible colors.";
    public IReadOnlyList<PluginCommand> Commands => [new("inspect", "Inspect image", "Count visible pixels and colors in the selected asset.")];

    public Task<string> ExecuteAsync(string commandId, PluginAsset asset, CancellationToken cancellationToken)
    {
        if (commandId != "inspect") throw new ArgumentException("Unknown command.", nameof(commandId));
        int visible = 0;
        var colors = new HashSet<int>();
        foreach (int pixel in asset.Pixels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)pixel >> 24 == 0) continue;
            visible++;
            colors.Add(pixel & 0xFFFFFF);
        }
        return Task.FromResult($"{asset.Kind} {asset.Id} (0x{asset.Id:X4})\n{asset.Width} × {asset.Height} pixels\nVisible pixels: {visible}\nTransparent pixels: {asset.Pixels.Length - visible}\nDistinct visible RGB colors: {colors.Count}");
    }
}
