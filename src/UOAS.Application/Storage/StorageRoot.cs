using UOAS.Contracts;

namespace UOAS.Application.Storage;

public sealed class StorageRoot
{
    public string Root { get; }
    public bool Configured => Root.Length > 0;
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public StorageRoot(string? root, string contentRoot)
    { Root = string.IsNullOrWhiteSpace(root) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, contentRoot)); }
    public static string Relative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > 1024 || Path.IsPathRooted(path) || path.Contains(':')) throw new ArgumentException("Use a path relative to the configured storage root.");
        path = path.Replace('\\', '/');
        if (path.Length == 0) return "";
        foreach (string part in path.Split('/'))
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))) throw new ArgumentException("The relative path contains an invalid segment.");
        return path;
    }
    private void RequireRoot()
    {
        if (!Configured) throw new InvalidOperationException("Configure Storage:RootDirectory on the backend before browsing or opening sources.");
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("The configured storage root does not exist.");
        if ((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("The configured storage root must be a physical directory.");
    }
    public string Resolve(string relative, bool allowMissing = false)
    {
        RequireRoot(); relative = Relative(relative);
        string candidate = Path.GetFullPath(Path.Combine(Root, relative));
        string prefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(Root, Comparison) && !candidate.StartsWith(prefix, Comparison)) throw new ArgumentException("The path is outside the configured storage root.");
        string current = Root;
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Symbolic links and junctions cannot be used as storage paths."); }
            catch (FileNotFoundException) when (allowMissing) { return candidate; }
            catch (DirectoryNotFoundException) when (allowMissing) { return candidate; }
        }
        return candidate;
    }
    public string FromConfiguredPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return Relative(path);
        RequireRoot();
        string relative = Path.GetRelativePath(Root, Path.GetFullPath(path)).Replace('\\', '/');
        return relative == "." ? "" : Relative(relative);
    }
    public string DirectoryPath(string relative)
    { string path = Resolve(relative); if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Select an existing storage directory."); return path; }
    public void ValidateLibrary(string relative)
    {
        string path = DirectoryPath(relative);
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Asset libraries cannot contain symbolic links or junctions.");
    }
    public StoragePage Browse(string path, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Choose a page size from 1 to 100.");
        path = Relative(path); string folder = DirectoryPath(path);
        var items = new DirectoryInfo(folder).EnumerateFileSystemInfos().Take(10001).ToArray();
        if (items.Length > 10000) throw new ArgumentException("This folder exceeds the browsing limit of 10,000 entries.");
        var entries = items.OrderByDescending(item => (item.Attributes & FileAttributes.Directory) != 0).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Skip(offset).Take(limit).Select(item =>
        {
            bool directory = (item.Attributes & FileAttributes.Directory) != 0, blocked = (item.Attributes & FileAttributes.ReparsePoint) != 0;
            return new StorageEntry(item.Name, path.Length == 0 ? item.Name : path + "/" + item.Name, directory, blocked, !directory && !blocked ? ((FileInfo)item).Length : null);
        }).ToArray();
        return new(path, path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "", items.Length, entries);
    }
}
