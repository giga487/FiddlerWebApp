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
                    throw new InvalidOperationException($"Unknown or duplicate catalog file: {file}");
            }
            result.Add(new ClientFileGroup(name, category,
                Array.AsReadOnly(alternatives.Select(a => (IReadOnlyList<string>)Array.AsReadOnly(a)).ToArray()), viewer));
        }
        Add("Land tiles and items", "Graphics", true, ["artidx.mul", "art.mul"], ["artlegacymul.uop"]);
        Add("Gump", "Graphics", true, ["gumpidx.mul", "gumpart.mul"], ["gumpartlegacymul.uop"]);
        Add("Textures", "Graphics", false, ["texidx.mul", "texmaps.mul"]);
        Add("Lights", "Graphics", false, ["lightidx.mul", "light.mul"]);
        Add("Sounds", "Audio", false, ["soundidx.mul", "sound.mul"], ["soundlegacymul.uop"]);
        Add("Multi", "Multi", true, ["multi.idx", "multi.mul"], ["multicollection.uop"]);
        Add("Skills", "Data", false, ["skills.idx", "skills.mul"]);
        for (int i = 0; i < 6; i++)
        {
            Add($"Map {i}", "Maps", true, [$"map{i}.mul"], [$"map{i}legacymul.uop"]);
            Add($"Map statics {i}", "Maps", false, [$"staidx{i}.mul", $"statics{i}.mul"]);
            Add($"Map patches {i}", "Optional patches", false, [$"mapdifl{i}.mul", $"mapdif{i}.mul"]);
            Add($"Static patches {i}", "Optional patches", false, [$"stadifl{i}.mul", $"stadifi{i}.mul", $"stadif{i}.mul"]);
        }
        for (int i = 1; i <= 6; i++)
        {
            string suffix = i == 1 ? "" : i.ToString();
            Add($"Animations {i}", "Animations", true,
                [$"anim{suffix}.idx", $"anim{suffix}.mul"], [$"animationframe{i}.uop"]);
        }
        foreach (string file in ExpectedFiles.Where(file => !assigned.Contains(file)))
        {
            string category = file.StartsWith("cliloc") ? "Localization" :
                file.StartsWith("unifont") || file == "fonts.mul" ? "Fonts" :
                file.StartsWith("facet") ? "Maps" : file.EndsWith(".exe") ? "Optional executables" :
                file.Contains("anim") || file.StartsWith("body") ? "Animations" : "Optional data";
            Add(file, category, false, [file]);
        }
        return result.AsReadOnly();
    }
}
