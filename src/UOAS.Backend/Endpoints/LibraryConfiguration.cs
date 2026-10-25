namespace UOAS.Backend.Endpoints;

public static class LibraryConfiguration
{
    public static string Directory(UOAS.Application.Storage.WorkspaceSettingsStore settings, string library) => settings.Library(library);
}
