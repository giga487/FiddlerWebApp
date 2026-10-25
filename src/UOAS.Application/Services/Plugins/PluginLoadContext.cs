using System.Reflection;
using System.Runtime.Loader;
using Fiddler.Plugin.Abstractions;

namespace FiddlerWebApp.Services.Plugins;

internal sealed class PluginLoadContext(string assemblyPath, bool loadFromMemory = false) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
    protected override Assembly? Load(AssemblyName name)
    {
        var contract = typeof(IFiddlerPlugin).Assembly;
        if (name.Name == contract.GetName().Name)
        {
            if (name.Version != contract.GetName().Version)
                throw new InvalidOperationException("The plugin references an incompatible contract assembly version.");
            return contract;
        }
        string? path = resolver.ResolveAssemblyToPath(name);
        if (path is null)
        {
            string local = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
            if (File.Exists(local)) path = local;
        }
        if (path is null) return null;
        if (!loadFromMemory) return LoadFromAssemblyPath(path);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        string? path = resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
