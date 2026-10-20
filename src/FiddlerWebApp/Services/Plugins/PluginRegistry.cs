using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Fiddler.Plugin.Abstractions;
using FiddlerWebApp.Services.Users;

namespace FiddlerWebApp.Services.Plugins;

public sealed record PluginInstallation(string Key, string FileName, string EntryAssembly, bool Enabled);
public sealed record PluginView(string Key, string FileName, bool Enabled, bool Loaded, string? Error,
    string? Id, string? Name, string? Version, string? Description, IReadOnlyList<PluginCommand> Commands);

/// <summary>Server-wide trusted assemblies; mutable command instances are never shared between users.</summary>
public sealed class PluginRegistry(string root, IConfiguration configuration, ILogger<PluginRegistry> logger) : IHostedService, IDisposable
{
    public const long MaxUploadBytes = 64 * 1024 * 1024;
    private const long MaxExtractedBytes = 128 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<PluginInstallation> installations = [];
    private readonly Dictionary<string, LoadedPlugin> loaded = [];
    private readonly Dictionary<string, string> errors = [];
    private string StatePath => Path.Combine(root, "registry.json");
    private sealed record LoadedPlugin(PluginLoadContext Context, Type Type, string Id, string Name,
        string Version, string Description, PluginCommand[] Commands);

    public bool CanManage(UserSession actor) => actor.IsSignedIn &&
        configuration.GetSection("Plugins:AdministratorIds").Get<string[]>()?.Contains(actor.User!.Id, StringComparer.Ordinal) == true;

    private void RequireAdministrator(UserSession actor)
    {
        actor.RequireUser();
        if (!CanManage(actor)) throw new UnauthorizedAccessException("Plugin management requires a configured administrator account.");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        if (!File.Exists(StatePath)) return;
        var saved = JsonSerializer.Deserialize<PluginInstallation[]>(await File.ReadAllTextAsync(StatePath, cancellationToken))
            ?? throw new InvalidDataException("Invalid plugin registry.");
        foreach (var item in saved)
        {
            if (!Guid.TryParseExact(item.Key, "N", out _)) throw new InvalidDataException("Invalid plugin installation key.");
            installations.Add(item);
            if (item.Enabled) TryLoad(item);
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IReadOnlyList<PluginView>> ListAsync(UserSession actor)
    {
        RequireAdministrator(actor);
        await gate.WaitAsync();
        try
        {
            RequireAdministrator(actor);
            return installations.Select(item =>
            {
                loaded.TryGetValue(item.Key, out var plugin);
                return new PluginView(item.Key, item.FileName, item.Enabled, plugin is not null, errors.GetValueOrDefault(item.Key),
                    plugin?.Id, plugin?.Name, plugin?.Version, plugin?.Description, plugin?.Commands.ToArray() ?? []);
            }).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task InstallAsync(UserSession actor, string fileName, string entryAssembly, Stream source)
    {
        RequireAdministrator(actor);
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".dll" or ".zip")) throw new InvalidDataException("Choose a .dll or .zip plugin package.");
        await gate.WaitAsync();
        string key = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(root, key);
        string upload = Path.Combine(root, key + ".upload");
        bool registered = false;
        try
        {
            RequireAdministrator(actor);
            Directory.CreateDirectory(folder);
            await using (var output = File.Create(upload)) await CopyLimitedAsync(source, output, MaxUploadBytes);
            string entry = extension == ".dll" ? Path.GetFileName(fileName) : entryAssembly.Trim();
            string mainPath = ResolveEntry(folder, entry);
            if (extension == ".dll") File.Move(upload, mainPath);
            else
            {
                using var archive = ZipFile.OpenRead(upload);
                if (archive.Entries.Count > 256) throw new InvalidDataException("The package supports at most 256 entries.");
                long total = 0;
                foreach (var zipEntry in archive.Entries)
                {
                    string destination = ResolveInside(folder, zipEntry.FullName);
                    if (zipEntry.FullName.EndsWith('/') || zipEntry.FullName.EndsWith('\\'))
                    { Directory.CreateDirectory(destination); continue; }
                    total = checked(total + zipEntry.Length);
                    if (total > MaxExtractedBytes) throw new InvalidDataException("The expanded package exceeds 128 MiB.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = zipEntry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew);
                    await CopyLimitedAsync(input, output, zipEntry.Length);
                }
            }
            if (!File.Exists(mainPath)) throw new InvalidDataException("The entry DLL was not found in the package.");
            // Metadata inspection does not instantiate the plugin. Activation is an explicit operation.
            AssemblyName.GetAssemblyName(mainPath);
            var item = new PluginInstallation(key, Path.GetFileName(fileName), entry, false);
            installations.Add(item);
            try { await SaveAsync(); }
            catch { installations.Remove(item); throw; }
            registered = true;
        }
        finally
        {
            try
            {
                if (File.Exists(upload)) File.Delete(upload);
                if (!registered && Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            finally { gate.Release(); }
        }
    }

    public async Task SetEnabledAsync(UserSession actor, string key, bool enabled)
    {
        RequireAdministrator(actor);
        await gate.WaitAsync();
        try
        {
            RequireAdministrator(actor);
            int index = installations.FindIndex(item => item.Key == key);
            if (index < 0) throw new KeyNotFoundException("Plugin installation not found.");
            var previous = installations[index];
            installations[index] = previous with { Enabled = enabled };
            try { await SaveAsync(); }
            catch { installations[index] = previous; throw; }
            if (loaded.Remove(key, out var old)) old.Context.Unload();
            errors.Remove(key);
            if (enabled) await Task.Run(() => TryLoad(installations[index]));
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(UserSession actor, string key)
    {
        RequireAdministrator(actor);
        await gate.WaitAsync();
        try
        {
            RequireAdministrator(actor);
            int index = installations.FindIndex(item => item.Key == key);
            if (index < 0) throw new KeyNotFoundException("Plugin installation not found.");
            var item = installations[index];
            if (item.Enabled) throw new InvalidOperationException("Deactivate the plugin before uninstalling it.");
            string folder = ResolveInside(root, key);
            // Assembly unloading is cooperative; removing the registry entry does not require immediate file deletion.
            installations.RemoveAt(index);
            try { await SaveAsync(); }
            catch { installations.Insert(index, item); throw; }
            errors.Remove(key);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException ex) { logger.LogWarning(ex, "Plugin {Key} was uninstalled; its files are still in use", key); }
            catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "Plugin {Key} was uninstalled; its files could not be removed", key); }
        }
        finally { gate.Release(); }
    }

    public async Task<string> ExecuteAsync(UserSession actor, string key, string commandId, PluginAsset asset, CancellationToken cancellationToken = default)
    {
        RequireAdministrator(actor);
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireAdministrator(actor);
            if (!loaded.TryGetValue(key, out var plugin)) throw new InvalidOperationException("Activate the plugin before running a command.");
            if (!plugin.Commands.Any(command => command.Id == commandId)) throw new ArgumentException("Unknown plugin command.");
            return await Task.Run(async () =>
            {
                var instance = (IFiddlerPlugin)Activator.CreateInstance(plugin.Type)!;
                try
                {
                    string result = await instance.ExecuteAsync(commandId, asset with { Pixels = asset.Pixels.ToArray() }, cancellationToken)
                        ?? throw new InvalidDataException("The plugin returned no result.");
                    return result.Length <= 65536 ? result : result[..65536] + "\n[Result truncated]";
                }
                finally { (instance as IDisposable)?.Dispose(); }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plugin command {Command} failed for installation {Key}", commandId, key);
            throw;
        }
        finally { gate.Release(); }
    }

    private void TryLoad(PluginInstallation item)
    {
        PluginLoadContext? context = null;
        try
        {
            string path = ResolveEntry(Path.Combine(root, item.Key), item.EntryAssembly);
            context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);
            var types = assembly.GetTypes().Where(type => typeof(IFiddlerPlugin).IsAssignableFrom(type) &&
                type.IsClass && !type.IsAbstract && type.IsPublic && type.GetConstructor(Type.EmptyTypes) is not null).ToArray();
            if (types.Length != 1) throw new InvalidDataException("The entry DLL must expose exactly one public IFiddlerPlugin with a parameterless constructor.");
            var instance = (IFiddlerPlugin)Activator.CreateInstance(types[0])!;
            try
            {
                if (instance.ApiVersion != 1) throw new InvalidDataException("Unsupported plugin API version. Expected version 1.");
                if (string.IsNullOrWhiteSpace(instance.Id) || string.IsNullOrWhiteSpace(instance.Name) || string.IsNullOrWhiteSpace(instance.Version))
                    throw new InvalidDataException("The plugin must declare its ID, name and version.");
                if (loaded.Values.Any(other => other.Id == instance.Id)) throw new InvalidDataException("A plugin with this ID is already active.");
                var commands = instance.Commands.ToArray();
                if (commands.Length is 0 or > 64 || commands.Any(command => command is null || string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Name)) ||
                    commands.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count() != commands.Length)
                    throw new InvalidDataException("The plugin must declare 1–64 commands with unique IDs and nonempty names.");
                loaded.Add(item.Key, new(context, types[0], instance.Id, instance.Name, instance.Version, instance.Description, commands));
            }
            finally { (instance as IDisposable)?.Dispose(); }
        }
        catch (Exception ex)
        {
            loaded.Remove(item.Key);
            context?.Unload();
            errors[item.Key] = ex.GetBaseException().Message;
            logger.LogError(ex, "Could not activate plugin installation {Key}", item.Key);
        }
    }

    private async Task SaveAsync()
    {
        string temporary = StatePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(installations));
        File.Move(temporary, StatePath, true);
    }
    private static string ResolveEntry(string folder, string entry)
    {
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Enter the relative path of the plugin entry DLL.");
        return ResolveInside(folder, entry);
    }
    private static string ResolveInside(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\'))
            throw new InvalidDataException("Package paths must be relative and use forward slashes.");
        foreach (string segment in relative.TrimEnd('/').Split('/'))
        {
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("The package contains an invalid path segment.");
        }
        string prefix = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(folder, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!destination.StartsWith(prefix, comparison)) throw new InvalidDataException("The package contains a path outside its installation folder.");
        return destination;
    }
    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("The plugin package exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }
    public void Dispose()
    {
        foreach (var plugin in loaded.Values) plugin.Context.Unload();
        loaded.Clear();
        gate.Dispose();
    }
}
