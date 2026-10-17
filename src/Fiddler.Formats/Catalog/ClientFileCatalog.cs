namespace Fiddler.Formats.Catalog;

public sealed record ClientFileGroup(string Name, string Category,
    IReadOnlyList<IReadOnlyList<string>> Alternatives, bool ViewerSupported = false);

/// <summary>
/// Snapshot of Ultima.Files._uoFiles in the workspace's UOFiddler fork.
/// Groups express alternatives and dependencies, not a universal client-version requirement.
/// </summary>
public static class ClientFileCatalog
{
    public static IReadOnlyList<string> ExpectedFiles { get; } = LoadNames();
    public static IReadOnlyList<ClientFileGroup> Groups { get; } = BuildGroups();

    private static IReadOnlyList<string> LoadNames()
    {
        using var stream = typeof(ClientFileCatalog).Assembly.GetManifestResourceStream(
            "Fiddler.Formats.Catalog.UOFiddlerFiles.txt")!;
        using var reader = new StreamReader(stream);
        return Array.AsReadOnly(reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<ClientFileGroup> BuildGroups()
    {
        var result = new List<ClientFileGroup>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string category, bool viewer, params string[][] alternatives)
        {
            foreach (var file in alternatives.SelectMany(a => a))
            {
                if (!ExpectedFiles.Contains(file, StringComparer.OrdinalIgnoreCase) || !assigned.Add(file))
                    throw new InvalidOperationException($"File sconosciuto o duplicato nel catalogo: {file}");
            }
            result.Add(new ClientFileGroup(name, category,
                Array.AsReadOnly(alternatives.Select(a => (IReadOnlyList<string>)Array.AsReadOnly(a)).ToArray()), viewer));
        }
        Add("Terreni e oggetti", "Grafica", true, ["artidx.mul", "art.mul"], ["artlegacymul.uop"]);
        Add("Gump", "Grafica", true, ["gumpidx.mul", "gumpart.mul"], ["gumpartlegacymul.uop"]);
        Add("Texture", "Grafica", false, ["texidx.mul", "texmaps.mul"]);
        Add("Luci", "Grafica", false, ["lightidx.mul", "light.mul"]);
        Add("Suoni", "Audio", false, ["soundidx.mul", "sound.mul"], ["soundlegacymul.uop"]);
        Add("Multi", "Multi", false, ["multi.idx", "multi.mul"], ["multicollection.uop"]);
        Add("Abilità", "Dati", false, ["skills.idx", "skills.mul"]);
        for (int i = 0; i < 6; i++)
        {
            Add($"Mappa {i}", "Mappe", false, [$"map{i}.mul"], [$"map{i}legacymul.uop"]);
            Add($"Statici mappa {i}", "Mappe", false, [$"staidx{i}.mul", $"statics{i}.mul"]);
            Add($"Patch mappa {i}", "Patch opzionali", false, [$"mapdifl{i}.mul", $"mapdif{i}.mul"]);
            Add($"Patch statici {i}", "Patch opzionali", false, [$"stadifl{i}.mul", $"stadifi{i}.mul", $"stadif{i}.mul"]);
        }
        for (int i = 1; i <= 6; i++)
        {
            string suffix = i == 1 ? "" : i.ToString();
            Add($"Animazioni {i}", "Animazioni", true,
                [$"anim{suffix}.idx", $"anim{suffix}.mul"], [$"animationframe{i}.uop"]);
        }
        foreach (string file in ExpectedFiles.Where(file => !assigned.Contains(file)))
        {
            string category = file.StartsWith("cliloc") ? "Localizzazione" :
                file.StartsWith("unifont") || file == "fonts.mul" ? "Font" :
                file.StartsWith("facet") ? "Mappe" : file.EndsWith(".exe") ? "Eseguibili opzionali" :
                file.Contains("anim") || file.StartsWith("body") ? "Animazioni" : "Dati opzionali";
            Add(file, category, false, [file]);
        }
        return result.AsReadOnly();
    }
}
