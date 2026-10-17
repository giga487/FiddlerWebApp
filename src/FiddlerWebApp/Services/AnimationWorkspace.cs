using Fiddler.Formats.Animations;

namespace FiddlerWebApp.Services;

public sealed class AnimationWorkspace : IDisposable
{
    public IReadOnlyList<AnimationMulReader> Readers { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public void Open(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("Configure the client data folder first.");
        var files = new DirectoryInfo(directory).EnumerateFiles().ToDictionary(file => file.Name, file => file.FullName, StringComparer.OrdinalIgnoreCase);
        var readers = new List<AnimationMulReader>();
        var warnings = new List<string>();
        try
        {
            for (int number = 1; number <= 6; number++)
            {
                string name = number == 1 ? "anim" : $"anim{number}";
                bool index = files.TryGetValue(name + ".idx", out var indexPath);
                bool data = files.TryGetValue(name + ".mul", out var dataPath);
                if (!index && !data) continue;
                if (!index || !data) { warnings.Add($"Incomplete {name} pair: missing {name}{(index ? ".mul" : ".idx")}."); continue; }
                try { readers.Add(AnimationMulReader.Open(indexPath!, dataPath!, number)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { warnings.Add($"{name}: {ex.Message}"); }
            }
            if (readers.Count == 0) throw new InvalidDataException(warnings.Count == 0
                ? "No anim.idx + anim.mul pair (or anim2–anim6) available. AnimationFrame UOP playback is not supported yet."
                : string.Join(" ", warnings));
        }
        catch { foreach (var reader in readers) reader.Dispose(); throw; }
        Dispose();
        Readers = readers.AsReadOnly();
        Warnings = warnings.AsReadOnly();
    }

    public void Dispose()
    {
        foreach (var reader in Readers) reader.Dispose();
        Readers = [];
        Warnings = [];
    }
}
