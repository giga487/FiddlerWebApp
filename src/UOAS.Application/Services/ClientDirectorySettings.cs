using System.Text.Json;

namespace FiddlerWebApp.Services;

/// <summary>Machine-local setting, persisted atomically outside the public web root.</summary>
public sealed class ClientDirectorySettings
{
    private readonly string storagePath;
    private readonly object gate = new();
    private string directory;
    public string Directory { get { lock (gate) return directory; } }

    public ClientDirectorySettings(string storagePath, string initialDirectory)
    {
        this.storagePath = storagePath;
        directory = File.Exists(storagePath)
            ? JsonSerializer.Deserialize<SavedDirectory>(File.ReadAllText(storagePath))?.Directory ?? initialDirectory
            : initialDirectory;
    }

    public void Save(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("The folder path must be absolute.");
        string normalized = Path.GetFullPath(value);
        if (!System.IO.Directory.Exists(normalized))
            throw new DirectoryNotFoundException("The folder no longer exists or is not accessible.");
        lock (gate)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            string temporary = storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedDirectory(normalized)));
                File.Move(temporary, storagePath, overwrite: true);
                directory = normalized;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private sealed record SavedDirectory(string Directory);
}
