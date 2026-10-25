using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UOAS.Contracts;

namespace UOAS.Application.Storage;

public sealed class WorkspaceSettingsStore(StorageRoot storage, IConfiguration configuration, string stateDirectory)
{
    private readonly object gate = new();
    private WorkspaceSettings? current;
    private string StateFile => Path.Combine(stateDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? storage.Root.ToUpperInvariant() : storage.Root))) + ".json");
    private static void Key(string id)
    { if (string.IsNullOrEmpty(id) || id.Length > 80 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("Source IDs must use letters, digits, hyphens or underscores."); }
    private static void Name(string name)
    { if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("Provide a source name from 1 to 100 characters."); }
    private WorkspaceSettings Initial()
    {
        var libraries = configuration.GetSection("Assets:Libraries").GetChildren().Where(section => !string.IsNullOrWhiteSpace(section["Directory"])).Select(section => new ConfiguredLibrary(section.Key, section["Name"] ?? section.Key, storage.FromConfiguredPath(section["Directory"]!))).ToArray();
        var regions = configuration.GetSection("Regions:Sources").GetChildren().Where(section => !string.IsNullOrWhiteSpace(section["File"]) || !string.IsNullOrWhiteSpace(section["Directory"])).Select(section => new ConfiguredRegionSource(section.Key, section["Name"] ?? section.Key, storage.FromConfiguredPath(!string.IsNullOrWhiteSpace(section["File"]) ? section["File"]! : Path.Combine(section["Directory"]!, "regions.json")), section.GetSection("Maps").Get<string[]>() ?? [], section.GetSection("Types").Get<string[]>() ?? [])).ToArray();
        return new(0, libraries, regions, storage.FromConfiguredPath(configuration["Assets:ExportDirectory"] ?? "Exports"));
    }
    public WorkspaceSettings Read()
    {
        lock (gate)
        {
            if (!storage.Configured) return new(0, [], [], "Exports");
            if (current is null)
            {
                WorkspaceSettings loaded;
                if (File.Exists(StateFile))
                {
                    if (new FileInfo(StateFile).Length > 256 * 1024) throw new InvalidDataException("The workspace configuration exceeds its size limit.");
                    loaded = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(StateFile)) ?? throw new InvalidDataException("Invalid workspace configuration.");
                }
                else loaded = Initial();
                Validate(loaded); current = loaded;
            }
            return current;
        }
    }
    private void Validate(WorkspaceSettings settings)
    {
        if (!storage.Configured) throw new InvalidOperationException("Configure Storage:RootDirectory on the backend first.");
        if (settings.Libraries is null || settings.RegionSources is null || settings.Libraries.Count > 32 || settings.RegionSources.Count > 32) throw new ArgumentException("Configure at most 32 libraries and 32 region sources.");
        if (settings.Libraries.Select(item => item.Id).Distinct().Count() != settings.Libraries.Count || settings.RegionSources.Select(item => item.Id).Distinct().Count() != settings.RegionSources.Count) throw new ArgumentException("Source IDs must be unique within their collection.");
        foreach (var library in settings.Libraries) { Key(library.Id); Name(library.Name); storage.ValidateLibrary(library.Directory); }
        foreach (var region in settings.RegionSources)
        {
            Key(region.Id); Name(region.Name); string file = storage.Resolve(region.File);
            if (!File.Exists(file) || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select an existing JSON file for a region source.");
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new ArgumentException("Region files must be at most 16 MiB.");
            _ = UOAS.Application.Regions.RegionDocument.Parse(File.ReadAllBytes(file));
            if (region.Maps is null || region.Types is null || region.Maps.Count > 64 || region.Types.Count > 128 || region.Maps.Concat(region.Types).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))) throw new ArgumentException("Invalid custom map or region type names.");
        }
        string output = storage.Resolve(settings.ExportDirectory, allowMissing: true);
        if (File.Exists(output)) throw new ArgumentException("The export path must be a directory.");
    }
    public WorkspaceSettings Save(WorkspaceSettings settings)
    {
        lock (gate)
        {
            var previous = Read();
            if (settings.Revision != previous.Revision) throw new InvalidOperationException("The configuration changed in another session. Reload it before saving.");
            Validate(settings);
            var saved = settings with { Revision = checked(previous.Revision + 1), Libraries = settings.Libraries.Select(item => item with { Directory = StorageRoot.Relative(item.Directory) }).ToArray(), RegionSources = settings.RegionSources.Select(item => item with { File = StorageRoot.Relative(item.File) }).ToArray(), ExportDirectory = StorageRoot.Relative(settings.ExportDirectory) };
            Directory.CreateDirectory(stateDirectory);
            string temporary = StateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(StateFile)) File.Copy(StateFile, StateFile + ".bak", true);
                File.Move(temporary, StateFile, true); current = saved; return saved;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public string Library(string id)
    { Key(id); var library = Read().Libraries.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("Library is not configured."); storage.ValidateLibrary(library.Directory); return storage.DirectoryPath(library.Directory); }
    public ConfiguredRegionSource Region(string id)
    { Key(id); return Read().RegionSources.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("Region source is not configured."); }
    public string RegionFile(string id) => storage.Resolve(Region(id).File);
    public string ExportDirectory()
    { string path = storage.Resolve(Read().ExportDirectory, allowMissing: true); Directory.CreateDirectory(path); return storage.DirectoryPath(Read().ExportDirectory); }
}
