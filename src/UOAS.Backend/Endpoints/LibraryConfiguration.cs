namespace UOAS.Backend.Endpoints;

public static class LibraryConfiguration
{
    public static string Directory(IConfiguration configuration, string library)
    {
        if (string.IsNullOrEmpty(library) || library.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) throw new ArgumentException("Select a configured library.");
        string path = configuration[$"Assets:Libraries:{library}:Directory"] ?? throw new KeyNotFoundException("Library is not configured on this backend.");
        if (string.IsNullOrWhiteSpace(path)) throw new KeyNotFoundException("Configure this library's directory on the backend before opening it.");
        return path;
    }
}
