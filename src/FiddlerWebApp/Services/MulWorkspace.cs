using Fiddler.Formats;

namespace FiddlerWebApp.Services;

/// <summary>Session-scoped archive ownership. No global mutable client profile.</summary>
public sealed class MulWorkspace : IDisposable
{
    private MulArchive? art;
    private MulArchive? gumps;
    public bool IsOpen => art is not null || gumps is not null;
    public string DisplayName { get; private set; } = "Nessun archivio aperto";

    public void Open(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Configura la cartella dati MUL prima di aprire l'archivio.");
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        if (!folder.Exists) throw new DirectoryNotFoundException("La cartella dati MUL non esiste.");
        var files = folder.EnumerateFiles().ToDictionary(file => file.Name, file => file.FullName,
            StringComparer.OrdinalIgnoreCase);
        MulArchive? nextArt = null, nextGumps = null;
        try
        {
            if (files.TryGetValue("artidx.mul", out var artIndex) && files.TryGetValue("art.mul", out var artData))
                nextArt = MulArchive.Open(artIndex, artData);
            if (files.TryGetValue("gumpidx.mul", out var gumpIndex) && files.TryGetValue("gumpart.mul", out var gumpData))
                nextGumps = MulArchive.Open(gumpIndex, gumpData, gumps: true);
            if (nextArt is null && nextGumps is null)
                throw new InvalidDataException("Nessuna coppia MUL trovata. Servono artidx.mul + art.mul oppure gumpidx.mul + gumpart.mul.");
        }
        catch
        {
            nextArt?.Dispose(); nextGumps?.Dispose();
            throw;
        }
        Dispose();
        art = nextArt; gumps = nextGumps;
        DisplayName = folder.Name;
    }

    public IReadOnlyList<MulEntry> Entries(AssetKind kind) =>
        (kind == AssetKind.Gump ? gumps : art)?.GetEntries(kind) ?? [];

    public DecodedImage Read(MulEntry entry) =>
        (entry.Kind == AssetKind.Gump ? gumps : art)?.Read(entry.Kind, entry.Id)
        ?? throw new InvalidOperationException("Archivio non aperto.");

    public void Dispose()
    {
        art?.Dispose(); gumps?.Dispose();
        art = null; gumps = null;
    }
}
