namespace Fiddler.Plugin.Abstractions;

public sealed record PluginCommand(string Id, string Name, string Description);

/// <summary>A detached image snapshot. Pixel values are straight-alpha ARGB8888.</summary>
public sealed record PluginAsset(string Kind, int Id, int Width, int Height, int[] Pixels);

/// <summary>Implement with a public parameterless constructor. Instances belong to one command invocation.</summary>
public interface IFiddlerPlugin
{
    int ApiVersion { get; }
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    IReadOnlyList<PluginCommand> Commands { get; }
    Task<string> ExecuteAsync(string commandId, PluginAsset asset, CancellationToken cancellationToken);
}
