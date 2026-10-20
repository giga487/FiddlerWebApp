namespace Fiddler.Formats.Catalog;

public enum ClientFileStatus { Present, Missing, Alternative, Empty, Unreadable, Ambiguous, Invalid }
public enum ClientGroupStatus { Available, Missing, Incomplete, Invalid }

public sealed record ClientFileResult(string Name, ClientFileStatus Status, long? Size,
    DateTime? ModifiedUtc, string? Detail = null, ClientFileGuidance? Guidance = null)
{
    public bool Usable => Status == ClientFileStatus.Present;
}

public sealed record ClientGroupResult(ClientFileGroup Definition, ClientGroupStatus Status,
    IReadOnlyList<ClientFileResult> Files, IReadOnlyList<string>? ActiveAlternative)
{
    public bool CanView => Definition.ViewerSupported && Status == ClientGroupStatus.Available &&
        (Definition.Name == "Gump" || Definition.Name == "Multi" || Definition.Name.StartsWith("Map ", StringComparison.Ordinal) ||
         Definition.Alternatives[0].All(name => Files.Any(file => file.Name == name && file.Usable)));
}

public sealed record ClientDirectoryReport(string Directory, DateTime ScannedUtc,
    IReadOnlyList<ClientGroupResult> Groups, IReadOnlyList<string> OtherFiles)
{
    public IEnumerable<ClientFileResult> Files => Groups.SelectMany(group => group.Files);
    public bool CanView => Groups.Any(group => group.CanView);
}

/// <summary>Read-only top-level inventory. Never loads game archives or recurses into subfolders.</summary>
public sealed class ClientDirectoryScanner
{
    public ClientDirectoryReport Scan(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim()))
            throw new ArgumentException("Enter an absolute folder path on the server.");
        string fullPath = Path.GetFullPath(directory.Trim());
        var folder = new DirectoryInfo(fullPath);
        if (!folder.Exists) throw new DirectoryNotFoundException("The folder does not exist or is not accessible on the server.");
        var found = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in folder.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!found.TryGetValue(file.Name, out var matches)) found[file.Name] = matches = [];
            matches.Add(file);
        }
        var groups = new List<ClientGroupResult>();
        foreach (var definition in ClientFileCatalog.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = definition.Alternatives.SelectMany(a => a).Select(name => Probe(name, found)).ToArray();
            var active = definition.Alternatives.FirstOrDefault(alternative =>
                alternative.All(name => results.Any(file => file.Name == name && file.Usable)));
            ClientGroupStatus status = active is not null ? ClientGroupStatus.Available :
                results.Any(file => file.Status is ClientFileStatus.Invalid or ClientFileStatus.Unreadable or
                    ClientFileStatus.Empty or ClientFileStatus.Ambiguous) ? ClientGroupStatus.Invalid :
                results.Any(file => file.Usable) ? ClientGroupStatus.Incomplete : ClientGroupStatus.Missing;
            if (active is not null)
                results = results.Select(file => file.Status == ClientFileStatus.Missing
                    ? file with { Status = ClientFileStatus.Alternative, Detail = "Covered by an available alternative format." }
                    : file).ToArray();
            var group = new ClientGroupResult(definition, status, Array.AsReadOnly(results), active);
            groups.Add(group with { Files = Array.AsReadOnly(results.Select(file =>
            {
                var guidance = ClientFileGuidance.Describe(group, file);
                return file with { Guidance = guidance, Detail = file.Status == ClientFileStatus.Alternative && guidance.Importance == ClientFileImportance.RequiredForFeature
                    ? "An alternative archive was found, but it does not provide this viewer feature." : file.Detail };
            }).ToArray()) });
        }
        var expected = ClientFileCatalog.ExpectedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ClientDirectoryReport(fullPath, DateTime.UtcNow, groups.AsReadOnly(),
            Array.AsReadOnly(found.Keys.Where(name => !expected.Contains(name)).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private static ClientFileResult Probe(string name, Dictionary<string, List<FileInfo>> found)
    {
        if (!found.TryGetValue(name, out var matches))
            return new(name, ClientFileStatus.Missing, null, null);
        if (matches.Count != 1)
            return new(name, ClientFileStatus.Ambiguous, null, null, "Multiple files differ only in letter case.");
        var file = matches[0];
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long size = stream.Length;
            bool index = name.EndsWith(".idx") || name is "artidx.mul" or "gumpidx.mul" or "texidx.mul" or
                "lightidx.mul" or "soundidx.mul" || name.StartsWith("staidx") || name.StartsWith("stadifi");
            if (index && size % 12 != 0)
                return new(name, ClientFileStatus.Invalid, size, file.LastWriteTimeUtc, "Index is not aligned to 12-byte records.");
            bool emptyAllowed = name.StartsWith("statics") || name.StartsWith("staidx") || name.StartsWith("stadif") ||
                name.StartsWith("mapdif") || name.EndsWith(".def") || name.EndsWith(".txt");
            if (size == 0 && !emptyAllowed)
                return new(name, ClientFileStatus.Empty, 0, file.LastWriteTimeUtc, "File exists but is empty.");
            return new(name, ClientFileStatus.Present, size, file.LastWriteTimeUtc,
                size == 0 ? "Empty: allowed for this optional resource." : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(name, ClientFileStatus.Unreadable, null, null, "Cannot read file: " + ex.Message); }
    }
}
