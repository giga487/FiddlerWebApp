# Server plugins

The Plugins page (`/plugins`) installs and manages trusted .NET 10 plugins.
The backend loads their assemblies; the browser uploads packages and displays
command results. This is a new contract, not binary compatibility with desktop
UOFiddler/WinForms plugins.

## Grant administrator access

Sign in and open `/plugins` to see your account ID. Add that exact ID to the
server configuration, then restart the application:

```json
{
  "Plugins": {
    "AdministratorIds": ["your-account-id"]
  }
}
```

Alternatively set the environment variable `Plugins__AdministratorIds__0` to
the account ID before starting the server. No account has access by default.
The checks apply inside the backend service to listing, installation,
activation, deactivation, uninstallation and command execution. A revoked or
expired session loses access. In authentication Open mode, all visitors share
one identity: granting that identity administrator access grants it to every
visitor who can reach the server.

## Try the example

Build the example without rebuilding the running web application:

```powershell
dotnet build examples/Fiddler.Plugin.AssetInfo/Fiddler.Plugin.AssetInfo.csproj -c Release
```

1. Open Plugins and select
   `examples/Fiddler.Plugin.AssetInfo/bin/Release/net10.0/Fiddler.Plugin.AssetInfo.dll`.
2. Check the trust acknowledgement and choose **Install inactive**.
3. Choose **Activate**. The plugin's name, version and commands appear.
4. Configure a client folder, select an asset type and decimal or hexadecimal ID
   on the Plugins page, then choose **Run on selected asset**.

The example reports dimensions, visible and transparent pixel counts, and the
number of distinct visible RGB colors. Results are rendered as plain text.
Deactivation prevents further commands. Deactivate before uninstalling.

## Implement a plugin

Reference `src/Fiddler.Plugin.Abstractions/Fiddler.Plugin.Abstractions.csproj`
and implement its `IFiddlerPlugin` interface with exactly one public concrete
class with a public parameterless constructor in the entry assembly. Return
`ApiVersion = 1`, a stable unique plugin ID, a name, a version, a description,
and 1–64 commands with unique IDs.

```csharp
using Fiddler.Plugin.Abstractions;

public sealed class MyPlugin : IFiddlerPlugin
{
    public int ApiVersion => 1;
    public string Id => "example.my-plugin";
    public string Name => "My plugin";
    public string Version => "1.0.0";
    public string Description => "Describe the selected image.";
    public IReadOnlyList<PluginCommand> Commands =>
        [new("describe", "Describe", "Show image dimensions.")];

    public Task<string> ExecuteAsync(string commandId, PluginAsset asset,
        CancellationToken cancellationToken)
    {
        if (commandId != "describe") throw new ArgumentException("Unknown command.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"{asset.Kind} {asset.Id}: {asset.Width} × {asset.Height}");
    }
}
```

The SDK supplies `PluginAsset`: asset kind, ID, dimensions and a detached
`int[]` of straight-alpha ARGB8888 pixels. Fully transparent pixels have alpha
zero. A fresh plugin instance is constructed per invocation; implement
`IDisposable` to release instance resources. Do not put user-specific state in
static fields: static state is shared on the server. Commands run on a worker
thread, serialized with lifecycle operations in this initial implementation.
Cancellation is cooperative; the loader cannot forcibly terminate plugin code.
Command output is limited to 65,536 characters. The contract currently returns
text and does not expose publishing, custom UI panels or service registration.

For dependencies, follow the example project: set `EnableDynamicLoading` to
`true` and reference the contract with `Private="false" ExcludeAssets="runtime"`.
Package the plugin output and dependencies as a ZIP, with no surrounding build
folder unless you include it in the entry path. Enter the relative entry DLL
path, such as `MyPlugin.dll` or `lib/MyPlugin.dll`, in the install form.
Use forward slashes in package paths. Managed dependencies are resolved through
`AssemblyDependencyResolver`, with a fallback for DLLs beside the entry DLL;
native dependencies require the matching dependency manifest and server OS.
The host shares its own contract assembly with the plugin.

## Lifecycle and operational limits

Packages are stored in unique folders under `App_Data/plugins`, outside public
static assets. `registry.json` persists installation paths and activation state.
Installation validates the entry assembly but does not construct the plugin.
Activation executes plugin code and validates its contract and command metadata.
Previously activated plugins are loaded on server startup; individual activation
failures appear on the Plugins page and in server logs. Duplicate active plugin
IDs are rejected. Failed plugins can be retried or deactivated.

Uploads are limited to 64 MiB, ZIPs to 256 entries and 128 MiB expanded data.
Paths outside an installation directory are rejected. Unloading is cooperative:
deactivation removes commands immediately, but the runtime may keep assembly
files open until collection. Uninstallation removes the registry entry and tries
to delete the package; any files still in use are logged and may need removal
after shutdown. To update a plugin, deactivate its old installation, install and
activate the replacement, then uninstall the old package.

An `AssemblyLoadContext` is a dependency/lifetime mechanism, not a security
sandbox. Plugins execute with the server process's filesystem, network and
runtime permissions. They can bypass the SDK's read-only workflow through their
own code. Install only trusted packages. Untrusted third-party execution would
require a separate restricted process or container.
