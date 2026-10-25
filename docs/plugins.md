# Plugin packages

The WASM `/plugins` page supports DLL and ZIP upload, package listing, activation,
deactivation, removal and command execution. Uploads are inert quarantined bytes:
no assembly is loaded or inspected through reflection at upload time.

A server operator must inspect the package and approve its exact SHA256 through
backend configuration before activation:

```json
{ "Plugins": { "TrustedPackageHashes": ["EXACT_SHA256_FROM_PACKAGE_LIST"] } }
```

Trust is rechecked at activation and every command. Removing a hash blocks further
execution immediately when configuration reloads. There is no browser approval
endpoint. This package trust policy is independent of the display-name sessions;
account authentication and authorization remain deferred.

Packages live outside public assets under backend `App_Data/packages`. Metadata
and uploaded bytes persist; activation is temporary and must be repeated after
restart. Limits are 32 packages, 8 MiB each, 128 ZIP entries and 64 MiB expanded
data. Relative paths, entry DLL presence and duplicate ZIP entries are validated.
ZIP traversal is rejected before extraction. Activation verifies the package
hash and extracts into an isolated installation directory; command execution
also verifies activated files. Duplicate active plugin IDs are rejected.

## SDK

Reference `src/Fiddler.Plugin.Abstractions` and implement `IFiddlerPlugin` with
one public concrete class and a public parameterless constructor. Return API
version 1, a stable plugin ID, descriptive metadata and 1–64 commands with unique
IDs. Package managed dependencies beside the entry DLL or with a dependency
manifest. ZIP entry DLL paths are relative, for example `lib/MyPlugin.dll`.

Each invocation constructs a new instance and receives an independent
`PluginAsset` containing kind, ID, dimensions and straight-alpha ARGB8888 pixels.
The backend retains control of its source archives and drafts. Output is plain
text, limited to 65,536 characters. Implement IDisposable to release resources;
cancellation is cooperative. Lifecycle operations and invocations are serialized.
Managed assemblies load into a collectible context from bytes to avoid holding
DLL file handles. Native dependencies may retain OS handles until shutdown.

Build the example:

```powershell
dotnet build examples/Fiddler.Plugin.AssetInfo/Fiddler.Plugin.AssetInfo.csproj -c Release
```

Upload its output DLL, approve the displayed hash in backend configuration,
activate it and run Inspect image on an asset ID. A ZIP may include dependencies.

An AssemblyLoadContext is not a security sandbox. Approved plugin code runs with
the backend process's filesystem, network and runtime permissions. Only approve
packages trusted to execute on that server. Isolation of untrusted code requires
a separate restricted process or container.
