# CollectiblePluginFramework

Reusable .NET 9 library for loading, hot-reloading, and unloading plugin assemblies via collectible `AssemblyLoadContext` instances.

## Highlights
- Load each plugin DLL into its own collectible context so it can be unloaded without restarting your app.
- Monitor a plugin directory and automatically reload assemblies when binaries change.
- Subscribe to strongly-typed events (`PluginLoaded`, `PluginReloaded`, `PluginUnloaded`, `ScanCompleted`) to react to catalog updates.
- Query loaded plugins at any time through lightweight `PluginSnapshot` records.

## Prerequisites
- .NET 9 SDK installed locally.
- Compiled plugin assemblies (DLLs) that target a compatible framework and can be resolved via standard dependency probing rules.

## Install the library

You can consume the project as a source reference while a package feed is not available:

```powershell
dotnet add <path-to-your-app>.csproj reference src/CollectiblePluginFramework/CollectiblePluginFramework.csproj
```

Alternatively, add the project to your solution and reference it through Visual Studio or `dotnet sln`.

## Quick start

```csharp
using CollectiblePluginFramework;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

using var catalog = new CollectiblePluginCatalog(
   loggerFactory.CreateLogger<CollectiblePluginCatalog>());

catalog.PluginLoaded += (_, args) =>
{
   Console.WriteLine($"Loaded {args.Plugin.FileName} => {args.Plugin.Assembly.FullName}");
};

catalog.PluginReloaded += (_, args) =>
{
   Console.WriteLine($"Reloaded {args.Plugin.FileName}; previous timestamp was {args.Previous?.LastWriteUtc:u}");
};

catalog.PluginUnloaded += (_, args) =>
{
   Console.WriteLine($"Unloaded {args.Plugin.FileName}");
};

catalog.ScanCompleted += (_, args) =>
{
   Console.WriteLine($"Scan completed (changes applied: {args.ChangesApplied}) at {args.LastScanUtc:u}");
};

catalog.SetDirectory(Path.Combine(AppContext.BaseDirectory, "plugins"));

foreach (var plugin in catalog.ListPlugins())
{
   Console.WriteLine($"Current plugin: {plugin.FileName}");
}

// Drop new or updated DLLs into the plugins folder to trigger automatic reloads.
```

By default the catalog watches the provided directory for `*.dll` changes and immediately performs a debounced rescan. Call `Dispose()` (or wrap in `using`) to tear down the watcher and unload all plugin contexts.

## Working with plugins

- `ListPlugins()` returns immutable `PluginSnapshot` instances that expose the file path, last write time, loaded `Assembly`, and the backing `AssemblyLoadContext` for advanced scenarios.
- `Rescan()` forces the catalog to re-evaluate the directory and emits events for any changes it detects.
- `Reload(string path)` replaces a single plugin even if the catalog watcher is disabled; `Unload(string path)` removes it entirely.
- `IsAssemblyLive(assembly)` lets you verify the catalog is still tracking an assembly before you invoke exported types.

Each plugin is read into memory before being loaded, so the source DLL can be overwritten on disk while the plugin is in use. The catalog also attempts to load a side-by-side PDB file if present to preserve symbols for debugging.

## Configuration options

```csharp
var options = new CollectiblePluginCatalogOptions
{
   SearchPattern = "*.Plugin.dll", // Limit which binaries are considered during scans
   EnableWatcher = true,           // Disable to control reloading manually
   WatcherDebounce = TimeSpan.FromMilliseconds(250),
   IsRelevantPath = path => !string.IsNullOrEmpty(path) && path.EndsWith("Plugin.dll", StringComparison.OrdinalIgnoreCase)
};

using var catalog = new CollectiblePluginCatalog(logger, options);
```

- `SearchPattern` controls which files are scanned (`*.dll` by default).
- `EnableWatcher` toggles the background `FileSystemWatcher`.
- `WatcherDebounce` tunes how long the catalog waits after a change before rescanning.
- `IsRelevantPath` provides a final filter for watcher events when you need more control than a glob.

## Building locally

```powershell
dotnet build CollectiblePluginFramework.sln
```

This produces the library assembly under `src/CollectiblePluginFramework/bin/Debug/net9.0/` by default.

## Project layout

```
CollectiblePluginFramework/
├─ README.md
├─ CollectiblePluginFramework.sln
└─ src/
   └─ CollectiblePluginFramework/
     ├─ CollectiblePluginFramework.csproj
     ├─ CollectiblePluginCatalog.cs
     ├─ CollectiblePluginCatalogOptions.cs
     └─ ... other framework sources
```
