using Fiddler.Formats.Fonts;
using Fiddler.Formats.Localization;
using Fiddler.Formats.Metadata;
using Fiddler.Formats;
using Fiddler.Formats.Hues;

namespace FiddlerWebApp.Services;

/// <summary>Resource documents and drafts are owned by one browser session.</summary>
public sealed class ClientResourceWorkspace
{
    public ClilocDocument? Cliloc { get; private set; }
    public FontDocument? Font { get; private set; }
    public TileDataDocument? TileData { get; private set; }
    public string ClilocDirectory { get; private set; } = "";
    public string FontDirectory { get; private set; } = "";
    public string TileDataDirectory => tileDirectory;
    private string tileDirectory = "";

    public static string[] Files(string directory, string pattern) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()
            .Where(name => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, true)).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    public static string FilePath(string directory, string file)
    {
        if (!Path.IsPathFullyQualified(directory) || string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file)
            throw new ArgumentException("Configure a client directory and select a file from that directory.");
        return Path.Combine(Path.GetFullPath(directory), file);
    }
    public void OpenCliloc(string directory, string file)
    {
        if (Cliloc?.DirtyCount > 0) throw new InvalidOperationException("Discard or export and discard the current Cliloc drafts before opening another file.");
        Cliloc = ClilocDocument.Open(FilePath(directory, file));
        ClilocDirectory = Path.GetFullPath(directory);
    }
    public void OpenFont(string directory, string file)
    {
        if (Font?.DirtyCount > 0) throw new InvalidOperationException("Discard or export and discard the current font drafts before opening another file.");
        Font = FontDocument.Open(FilePath(directory, file));
        FontDirectory = Path.GetFullPath(directory);
    }
    public void OpenTileData(string directory, TileDataLayout layout = TileDataLayout.Auto)
    {
        if (TileData?.DirtyCount > 0) throw new InvalidOperationException("Discard or export and discard TileData drafts before reopening.");
        string name = Files(directory, "tiledata.mul").SingleOrDefault() ?? throw new FileNotFoundException("tiledata.mul was not found in the configured folder.");
        var document = TileDataDocument.Open(FilePath(directory, name), layout);
        TileData = document; tileDirectory = Path.GetFullPath(directory);
    }
    public TileDataDocument? TileDataFor(string directory)
    {
        if (TileData is not null && string.Equals(tileDirectory, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return TileData;
        // A different account folder must not inherit the previous folder's metadata or drafts.
        if (TileData?.DirtyCount > 0) return null;
        if (Files(directory, "tiledata.mul").Length == 0) return null;
        OpenTileData(directory); return TileData;
    }
    public HueMode HueModeFor(string directory, AssetKind kind, int id)
    {
        if (kind != AssetKind.Item) return HueMode.Full;
        var document = TileDataFor(directory);
        return document is not null && id >= 0 && id < document.ItemCount && document.Read(kind, id).PartialHue ? HueMode.GrayPixelsOnly : HueMode.Full;
    }
}
