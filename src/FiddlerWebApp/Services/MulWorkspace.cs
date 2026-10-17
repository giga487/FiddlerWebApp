using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;

namespace FiddlerWebApp.Services;

/// <summary>Session-scoped archive ownership. No global mutable client profile.</summary>
public sealed class MulWorkspace : IDisposable
{
    private ArtMulReader? art;
    private GumpDocument? gumps;
    public GumpDocument? Gumps => gumps;
    public string CurrentDirectory { get; private set; } = "";
    public bool IsOpen => art is not null || gumps is not null;
    public string DisplayName { get; private set; } = "No archive open";

    public void Open(string directory, bool discardChanges = false)
    {
        if (gumps?.HasChanges == true && !discardChanges)
            throw new InvalidOperationException("There are Gump drafts: save a version or discard changes before opening another archive.");
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Configure the MUL data folder before opening the archive.");
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        if (!folder.Exists) throw new DirectoryNotFoundException("The MUL data folder does not exist.");
        var files = folder.EnumerateFiles().ToDictionary(file => file.Name, file => file.FullName,
            StringComparer.OrdinalIgnoreCase);
        ArtMulReader? nextArt = null;
        GumpDocument? nextGumps = null;
        try
        {
            if (files.TryGetValue("artidx.mul", out var artIndex) && files.TryGetValue("art.mul", out var artData))
                nextArt = ArtMulReader.Open(artIndex, artData);
            if (files.ContainsKey("gumpidx.mul") && files.ContainsKey("gumpart.mul") || files.ContainsKey("gumpartlegacymul.uop"))
                nextGumps = GumpDocument.Open(folder.FullName);
            if (nextArt is null && nextGumps is null)
                throw new InvalidDataException("No MUL pair found. Expected artidx.mul + art.mul or gumpidx.mul + gumpart.mul.");
        }
        catch
        {
            nextArt?.Dispose(); nextGumps?.Dispose();
            throw;
        }
        Dispose();
        art = nextArt; gumps = nextGumps;
        DisplayName = folder.Name;
        CurrentDirectory = folder.FullName;
    }

    public IReadOnlyList<MulEntry> Entries(AssetKind kind) => kind switch
    {
        AssetKind.Land => art?.LandEntries ?? [],
        AssetKind.Item => art?.ItemEntries ?? [],
        AssetKind.Gump => gumps?.Entries ?? [],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public DecodedImage Read(MulEntry entry) => (entry.Kind switch
    {
        AssetKind.Land => art?.ReadLand(entry.Id),
        AssetKind.Item => art?.ReadItem(entry.Id),
        AssetKind.Gump => gumps?.Read(entry.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(entry))
    }) ?? throw new InvalidOperationException("Archive is not open.");

    public void Dispose()
    {
        art?.Dispose(); gumps?.Dispose();
        art = null; gumps = null;
    }
}
