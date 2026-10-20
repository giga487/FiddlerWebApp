namespace Fiddler.Formats.Editing;

public static class ResourceExport
{
    public static string Write(string parent, string prefix, Action<string> write)
    {
        if (!Path.IsPathFullyQualified(parent)) throw new ArgumentException("Enter an absolute output folder on the server.");
        string key = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(Path.GetFullPath(parent), prefix + "-" + key[..12]);
        string stage = Path.Combine(Path.GetFullPath(parent), ".stage-" + key);
        Directory.CreateDirectory(stage);
        try { write(stage); Directory.Move(stage, destination); return destination; }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static byte[] Read(string path, int limit)
    {
        using var source = File.OpenRead(path);
        if (source.Length > limit) throw new InvalidDataException("Resource exceeds the supported size limit.");
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        return bytes;
    }
}
