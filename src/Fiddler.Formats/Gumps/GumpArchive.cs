namespace Fiddler.Formats.Gumps;

public static class GumpArchive
{
    public static IGumpArchive Open(string path, string? indexPath = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Enter an absolute path on the server.");
        if (Directory.Exists(path))
        {
            var files = new DirectoryInfo(path).EnumerateFiles().ToDictionary(file => file.Name, file => file.FullName, StringComparer.OrdinalIgnoreCase);
            if (files.TryGetValue("gumpartlegacymul.uop", out var uop)) return GumpUopReader.Open(uop);
            if (files.TryGetValue("gumpart.mul", out var data) && files.TryGetValue("gumpidx.mul", out var index)) return GumpMulReader.Open(index, data);
            throw new InvalidDataException("No Gump MUL/IDX or UOP archive in the folder.");
        }
        if (Path.GetExtension(path).Equals(".uop", StringComparison.OrdinalIgnoreCase)) return GumpUopReader.Open(path);
        if (!Path.GetExtension(path).Equals(".mul", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select a .mul or .uop archive.");
        if (string.IsNullOrWhiteSpace(indexPath)) indexPath = new DirectoryInfo(Path.GetDirectoryName(path)!).EnumerateFiles()
            .FirstOrDefault(file => file.Name.Equals("gumpidx.mul", StringComparison.OrdinalIgnoreCase))?.FullName
            ?? Path.Combine(Path.GetDirectoryName(path)!, "gumpidx.mul");
        if (!Path.IsPathFullyQualified(indexPath)) throw new ArgumentException("The index path must be absolute.");
        return GumpMulReader.Open(indexPath, path);
    }
}
