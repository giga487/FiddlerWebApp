using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Fiddler.Plugin.Abstractions;
using FiddlerWebApp.Services.Plugins;
using UOAS.Contracts;

namespace UOAS.Application.Collaboration;

/// <summary>Uploads are inert until an operator approves their exact package hash in server configuration.</summary>
public sealed class PluginPackages(string root, IConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Loaded> active = [];
    private sealed record Installation(string Key, string FileName, string EntryAssembly, string Sha256);
    private sealed record Loaded(PluginLoadContext Context, Type Type, string Id, string Name, string Version, string Description, PackageCommand[] Commands, Dictionary<string, string> Files);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private bool Trusted(Installation item) => (configuration.GetSection("Plugins:TrustedPackageHashes").Get<string[]>() ?? []).Contains(item.Sha256, StringComparer.OrdinalIgnoreCase);
    private string Folder(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Invalid plugin package key.");
        return Path.Combine(root, key);
    }
    private Installation Read(string key)
    {
        var item = JsonSerializer.Deserialize<Installation>(File.ReadAllText(Path.Combine(Folder(key), "package.json"))) ?? throw new InvalidDataException("Invalid package metadata.");
        if (item.Key != key) throw new InvalidDataException("Invalid package key.");
        return item;
    }
    private PluginPackage View(Installation item)
    {
        active.TryGetValue(item.Key, out var loaded);
        return new(item.Key, item.FileName, item.EntryAssembly, item.Sha256, Trusted(item), loaded is not null && Trusted(item), loaded?.Name, loaded?.Version, loaded?.Description, loaded?.Commands ?? []);
    }
    public async Task<IReadOnlyList<PluginPackage>> List()
    {
        await gate.WaitAsync();
        try { Directory.CreateDirectory(root); return Directory.EnumerateDirectories(root).Where(path => File.Exists(Path.Combine(path, "package.json"))).Select(path => View(Read(Path.GetFileName(path)))).ToArray(); }
        finally { gate.Release(); }
    }
    private static string Relative(string name)
    {
        string normalized = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200 || normalized.Split('/').Any(part => part is "" or "." or ".." || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || part.EndsWith('.') || part.EndsWith(' '))) throw new ArgumentException("Package paths must be relative file names without traversal.");
        return normalized;
    }
    public async Task<PluginPackage> Install(InstallPluginRequest request)
    {
        if (request.ContentBase64.Length > 12 * 1024 * 1024) throw new ArgumentException("Plugin packages must be at most 8 MiB.");
        string file = Relative(request.FileName);
        if (file.Contains('/')) throw new ArgumentException("Provide a package file name.");
        string extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".dll" or ".zip")) throw new ArgumentException("Upload a DLL or ZIP package.");
        string entry = Relative(extension == ".dll" ? file : request.EntryAssembly);
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose the DLL entry assembly inside the package.");
        byte[] data = Convert.FromBase64String(request.ContentBase64);
        if (data.Length == 0 || data.Length > 8 * 1024 * 1024) throw new ArgumentException("Plugin packages must be at most 8 MiB.");
        await gate.WaitAsync();
        string key = Guid.NewGuid().ToString("N"), folder = Folder(key);
        try
        {
            Directory.CreateDirectory(root);
            if (Directory.EnumerateDirectories(root).Count() >= 32) throw new InvalidOperationException("Remove a package before installing more than 32 packages.");
            // Validate ZIP names and expansion limits without loading any assembly.
            if (extension == ".zip")
            {
                using var zip = new ZipArchive(new MemoryStream(data));
                if (zip.Entries.Count > 128 || zip.Entries.Sum(item => item.Length) > 64 * 1024 * 1024) throw new ArgumentException("The expanded package exceeds its limits.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in zip.Entries)
                {
                    if (item.FullName.EndsWith('/')) { Relative(item.FullName.TrimEnd('/')); continue; }
                    if (!names.Add(Relative(item.FullName))) throw new ArgumentException("Duplicate package entry.");
                }
                if (!names.Contains(entry)) throw new ArgumentException("The entry assembly was not found inside the ZIP.");
            }
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "upload.bin"), data);
            var install = new Installation(key, file, entry, Hash(data));
            File.WriteAllText(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(install));
            return View(install);
        }
        finally { gate.Release(); }
    }
    private byte[] Verified(Installation item)
    {
        if (!Trusted(item)) throw new InvalidOperationException("This package is quarantined. A server operator must add its SHA256 to Plugins:TrustedPackageHashes before activation.");
        byte[] bytes = File.ReadAllBytes(Path.Combine(Folder(item.Key), "upload.bin"));
        if (Hash(bytes) != item.Sha256) throw new InvalidDataException("The uploaded package hash changed.");
        return bytes;
    }
    public async Task<PluginPackage> Activate(string key)
    {
        await gate.WaitAsync();
        try
        {
            var item = Read(key); byte[] bytes = Verified(item);
            if (active.ContainsKey(key)) return View(item);
            foreach (string previous in Directory.GetDirectories(Folder(key), "runtime-*")) Directory.Delete(previous, recursive: true);
            string target = Path.Combine(Folder(key), "runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
            if (Path.GetExtension(item.FileName).Equals(".dll", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(Path.Combine(target, item.EntryAssembly), bytes);
            else
            {
                using var zip = new ZipArchive(new MemoryStream(bytes));
                foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/')))
                {
                    string output = Path.Combine(target, Relative(entry.FullName)); Directory.CreateDirectory(Path.GetDirectoryName(output)!); entry.ExtractToFile(output);
                }
            }
            string assembly = Path.Combine(target, Relative(item.EntryAssembly));
            var context = new PluginLoadContext(assembly, loadFromMemory: true);
            try
            {
                using var assemblyBytes = new MemoryStream(File.ReadAllBytes(assembly));
                var types = context.LoadFromStream(assemblyBytes).GetTypes().Where(type => !type.IsAbstract && typeof(IFiddlerPlugin).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null).ToArray();
                if (types.Length != 1) throw new ArgumentException("A package must contain exactly one public plugin implementation.");
                var plugin = (IFiddlerPlugin)Activator.CreateInstance(types[0])!;
                try
                {
                    if (plugin.ApiVersion != 1 || string.IsNullOrWhiteSpace(plugin.Id) || plugin.Commands.Count is < 1 or > 64 || plugin.Commands.Any(command => string.IsNullOrWhiteSpace(command.Id)) || plugin.Commands.Select(command => command.Id).Distinct().Count() != plugin.Commands.Count) throw new ArgumentException("Invalid plugin contract or commands.");
                    if (active.Values.Any(loaded => loaded.Id == plugin.Id)) throw new InvalidOperationException("Deactivate the existing installation of this plugin before activating its replacement.");
                    active[key] = new(context, types[0], plugin.Id, plugin.Name, plugin.Version, plugin.Description, plugin.Commands.Select(command => new PackageCommand(command.Id, command.Name, command.Description)).ToArray(), Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => Hash(File.ReadAllBytes(path))));
                }
                finally { (plugin as IDisposable)?.Dispose(); }
            }
            catch { context.Unload(); throw; }
            return View(item);
        }
        finally { gate.Release(); }
    }
    public async Task Deactivate(string key, bool remove = false)
    {
        await gate.WaitAsync();
        try
        {
            _ = Read(key);
            if (active.Remove(key, out var plugin)) plugin.Context.Unload();
            if (remove) Directory.Delete(Folder(key), recursive: true);
        }
        finally { gate.Release(); }
    }
    public async Task<PluginOutput> Execute(string key, string command, PluginAsset asset, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            _ = Verified(Read(key));
            if (!active.TryGetValue(key, out var loaded)) throw new InvalidOperationException("Activate this trusted plugin first.");
            if (!loaded.Commands.Any(item => item.Id == command)) throw new ArgumentException("Unknown plugin command.");
            if (loaded.Files.Any(file => Hash(File.ReadAllBytes(file.Key)) != file.Value)) throw new InvalidDataException("The activated plugin files changed.");
            var instance = (IFiddlerPlugin)Activator.CreateInstance(loaded.Type)!;
            try { string text = await instance.ExecuteAsync(command, asset with { Pixels = asset.Pixels.ToArray() }, token); return new(text.Length <= 65536 ? text : text[..65536] + " [truncated]"); }
            finally { (instance as IDisposable)?.Dispose(); }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { foreach (var plugin in active.Values) plugin.Context.Unload(); gate.Dispose(); }
}
