using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Maps;
using Fiddler.Formats.Multis;
using FiddlerWebApp.Services.Users;

namespace FiddlerWebApp.Services;

/// <summary>Session-owned readers, independent of the editable Gump workspace.</summary>
public sealed class WorldAssetWorkspace : IDisposable
{
    private readonly UserSession? user;
    public WorldAssetWorkspace() { }
    public WorldAssetWorkspace(UserSession user) => this.user = user;
    private ArtArchive? art;
    public MultiArchive? Multis { get; private set; }
    public MapArchive? Map { get; private set; }
    public string Directory { get; private set; } = "";
    public bool HasArt => art is not null;
    public string ArtFormat => art?.Format ?? "Art missing: magenta placeholders";
    public DecodedImage ReadArt(int id, bool land) => art?.Read(id, land) ?? throw new KeyNotFoundException("art.mul + artidx.mul or artLegacyMUL.uop is required.");
    public void OpenMultis(string directory, bool? preferUop, int rowSize)
    {
        user?.RequireUser();
        var files = Files(directory);
        bool useUop = preferUop ?? files.ContainsKey("multicollection.uop");
        ArtArchive? nextArt = null;
        MultiArchive? next = null;
        try
        {
            nextArt = OpenArt(files);
            string data = Required(files, useUop ? "multicollection.uop" : "multi.mul");
            next = MultiArchive.Open(data, useUop ? null : Required(files, "multi.idx"), rowSize);
        }
        catch { nextArt?.Dispose(); next?.Dispose(); throw; }
        Dispose(); art = nextArt; Multis = next; Directory = Path.GetFullPath(directory);
    }
    public void OpenMap(string directory, int facet, bool? preferUop, bool includeStatics)
    {
        user?.RequireUser();
        var files = Files(directory);
        bool useUop = preferUop ?? files.ContainsKey($"map{facet}legacymul.uop");
        ArtArchive? nextArt = null;
        MapArchive? next = null;
        try
        {
            nextArt = OpenArt(files);
            string data = Required(files, useUop ? $"map{facet}legacymul.uop" : $"map{facet}.mul");
            files.TryGetValue($"staidx{facet}.mul", out string? index);
            files.TryGetValue($"statics{facet}.mul", out string? statics);
            next = MapArchive.Open(data, facet, includeStatics ? index : null, includeStatics ? statics : null);
        }
        catch { nextArt?.Dispose(); next?.Dispose(); throw; }
        Dispose(); art = nextArt; Map = next; Directory = Path.GetFullPath(directory);
    }
    private static Dictionary<string, string> Files(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("Configure an absolute folder path on the server.");
        return new DirectoryInfo(directory).EnumerateFiles().ToDictionary(f => f.Name, f => f.FullName, StringComparer.OrdinalIgnoreCase);
    }
    private static string Required(Dictionary<string, string> files, string name) =>
        files.TryGetValue(name, out var path) ? path : throw new FileNotFoundException($"Missing {name} in the configured folder.");
    private static ArtArchive? OpenArt(Dictionary<string, string> files)
    {
        if (files.TryGetValue("artlegacymul.uop", out var uop)) return ArtArchive.Open(uop);
        if (files.TryGetValue("art.mul", out var data) && files.TryGetValue("artidx.mul", out var index)) return ArtArchive.Open(data, index);
        return null;
    }
    public void Dispose() { art?.Dispose(); Multis?.Dispose(); Map?.Dispose(); art = null; Multis = null; Map = null; }
}
