using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace CollectiblePluginFramework;

/// <summary>
/// Manages collectible plugin assemblies loaded from disk into dedicated AssemblyLoadContexts.
/// </summary>
public sealed partial class CollectiblePluginCatalog(ILogger? logger = null, CollectiblePluginCatalogOptions? options = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CollectiblePluginCatalogOptions _options = options ?? new CollectiblePluginCatalogOptions();
    private readonly Dictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounceTimer;
    private FileSystemEventHandler? _watcherHandler;
    private RenamedEventHandler? _watcherRenamedHandler;
    private bool _disposed;

    private string _pluginsDir = string.Empty;
    private DateTime _lastScanUtc = DateTime.MinValue;

    public event EventHandler<PluginLoadedEventArgs>? PluginLoaded;
    public event EventHandler<PluginReloadedEventArgs>? PluginReloaded;
    public event EventHandler<PluginUnloadedEventArgs>? PluginUnloaded;
    public event EventHandler<PluginCatalogScanCompletedEventArgs>? ScanCompleted;

    public string PluginsDirectory
    {
        get
        {
            using (_gate.EnterScope())
            {
                return _pluginsDir;
            }
        }
    }

    public DateTime LastScanUtc
    {
        get
        {
            using (_gate.EnterScope())
            {
                return _lastScanUtc;
            }
        }
    }

    public IReadOnlyCollection<PluginSnapshot> ListPlugins()
    {
        using (_gate.EnterScope())
        {
            return _plugins.Values.Select(static p => p.CreateSnapshot()).ToArray();
        }
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    public void SetDirectory(string directory, bool forceReload = false)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CollectiblePluginCatalog));
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory must be provided.", nameof(directory));

        var resolved = Path.GetFullPath(directory);
        var pending = new List<PendingEvent>();
        bool scanWasRun = false;

        using (_gate.EnterScope())
        {
            if (!forceReload && string.Equals(_pluginsDir, resolved, StringComparison.OrdinalIgnoreCase))
                return;

            StopWatcherLocked();

            if (_plugins.Count > 0)
            {
                UnloadAllLocked(pending);
            }

            _pluginsDir = resolved;
            Directory.CreateDirectory(_pluginsDir);

            scanWasRun = RescanLocked(pending);
            StartWatcherLocked();
        }

        RaiseEvents(pending);
        if (scanWasRun)
            ScanCompleted?.Invoke(this, new PluginCatalogScanCompletedEventArgs(true, _lastScanUtc));
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    public bool Rescan()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CollectiblePluginCatalog));

        var pending = new List<PendingEvent>();
        bool changed;

        using (_gate.EnterScope())
        {
            changed = RescanLocked(pending);
        }

        RaiseEvents(pending);
        ScanCompleted?.Invoke(this, new PluginCatalogScanCompletedEventArgs(changed, _lastScanUtc));
        return changed;
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    public bool Reload(string fileOrPath)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CollectiblePluginCatalog));
        if (string.IsNullOrWhiteSpace(fileOrPath))
            return false;

        var pending = new List<PendingEvent>();
        bool result;

        using (_gate.EnterScope())
        {
            result = LoadOrReloadLocked(ResolvePathLocked(fileOrPath), pending, force: true);
        }

        RaiseEvents(pending);
        if (result)
            ScanCompleted?.Invoke(this, new PluginCatalogScanCompletedEventArgs(true, _lastScanUtc));
        return result;
    }

    public bool Unload(string fileOrPath)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CollectiblePluginCatalog));
        if (string.IsNullOrWhiteSpace(fileOrPath))
            return false;

        var pending = new List<PendingEvent>();
        bool result;

        using (_gate.EnterScope())
        {
            result = UnloadLocked(ResolvePathLocked(fileOrPath), pending);
        }

        RaiseEvents(pending);
        if (result)
            ScanCompleted?.Invoke(this, new PluginCatalogScanCompletedEventArgs(true, _lastScanUtc));
        return result;
    }

    public bool IsAssemblyLive(Assembly assembly)
    {
        if (assembly is null)
            throw new ArgumentNullException(nameof(assembly));

        var alc = AssemblyLoadContext.GetLoadContext(assembly);
        if (alc == AssemblyLoadContext.Default)
            return true;

        using (_gate.EnterScope())
        {
            foreach (var entry in _plugins.Values)
            {
                if (ReferenceEquals(entry.Context, alc))
                    return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var pending = new List<PendingEvent>();

        using (_gate.EnterScope())
        {
            StopWatcherLocked();
            UnloadAllLocked(pending);
            _plugins.Clear();
        }

        RaiseEvents(pending);
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    private bool RescanLocked(List<PendingEvent> pending)
    {
        if (string.IsNullOrWhiteSpace(_pluginsDir))
            return false;

        var pattern = string.IsNullOrWhiteSpace(_options.SearchPattern) ? "*.dll" : _options.SearchPattern;
        var files = Directory.Exists(_pluginsDir)
            ? Directory.EnumerateFiles(_pluginsDir, pattern, SearchOption.TopDirectoryOnly).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var removed = _plugins.Keys.Where(path => !files.Contains(path)).ToList();
        foreach (var path in removed)
        {
            UnloadLocked(path, pending);
        }

        bool changed = removed.Count > 0;

        foreach (var dll in files)
        {
            var result = LoadOrReloadLocked(dll, pending, force: false);
            changed |= result;
        }

        _lastScanUtc = DateTime.UtcNow;
        return changed;
    }

    private string? ResolvePathLocked(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (Path.IsPathRooted(input))
            return File.Exists(input) ? Path.GetFullPath(input) : input;

        if (string.IsNullOrWhiteSpace(_pluginsDir))
            return null;

        var combined = Path.Combine(_pluginsDir, input);
        if (File.Exists(combined))
            return Path.GetFullPath(combined);

        var name = Path.GetFileName(input);
        var match = _plugins.Keys.FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    private bool LoadOrReloadLocked(string? dllPath, List<PendingEvent> pending, bool force)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            return false;
        if (!File.Exists(dllPath))
            return false;

        dllPath = Path.GetFullPath(dllPath);
        var lastWrite = File.GetLastWriteTimeUtc(dllPath);

        if (_plugins.TryGetValue(dllPath, out var existing))
        {
            if (!force && lastWrite <= existing.LastWriteUtc)
                return false;

            var previous = existing.CreateSnapshot();
            UnloadContext(existing);
            _plugins.Remove(dllPath);

            var loaded = LoadPluginEntry(dllPath, lastWrite);
            _plugins[dllPath] = loaded;
            pending.Add(new PendingEvent(PluginChangeKind.Reloaded, loaded.CreateSnapshot(), previous));
            return true;
        }

        var entry = LoadPluginEntry(dllPath, lastWrite);
        _plugins[dllPath] = entry;
        pending.Add(new PendingEvent(PluginChangeKind.Loaded, entry.CreateSnapshot(), null));
        return true;
    }

    private bool UnloadLocked(string? dllPath, List<PendingEvent> pending)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            return false;
        if (!_plugins.TryGetValue(dllPath, out var entry))
            return false;

        _plugins.Remove(dllPath);
        var snapshot = entry.CreateSnapshot();
        UnloadContext(entry);
        pending.Add(new PendingEvent(PluginChangeKind.Unloaded, snapshot, snapshot));
        return true;
    }

    private void UnloadAllLocked(List<PendingEvent> pending)
    {
        var paths = _plugins.Keys.ToList();
        foreach (var path in paths)
        {
            UnloadLocked(path, pending);
        }
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    private PluginEntry LoadPluginEntry(string dllPath, DateTime lastWriteUtc)
    {
        var ctx = new PluginLoadContext(dllPath);
        using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        ms.Position = 0;

        Stream? pdbStream = null;
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (!string.IsNullOrWhiteSpace(pdbPath) && File.Exists(pdbPath))
            pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));

        Assembly assembly;
        try
        {
            assembly = ctx.LoadFromStream(ms, pdbStream);
        }
        finally
        {
            pdbStream?.Dispose();
        }

        return new PluginEntry(dllPath, lastWriteUtc, ctx, assembly, new WeakReference(ctx, false));
    }

    private void UnloadContext(PluginEntry entry)
    {
        try
        {
            entry.Context.Unload();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Plugin context unload threw for {Dll}", Path.GetFileName(entry.Path));
        }

        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        logger?.LogInformation("Unloaded plugin: {Dll}", Path.GetFileName(entry.Path));
    }

    [RequiresUnreferencedCode("Plugin loading requires preserved metadata.")]
    private void StartWatcherLocked()
    {
        if (!_options.EnableWatcher)
            return;
        if (string.IsNullOrWhiteSpace(_pluginsDir))
            return;
        if (!Directory.Exists(_pluginsDir))
            return;

        _debounceTimer = new System.Timers.Timer(Math.Max(50, _options.WatcherDebounce.Milliseconds > 0 ? (int)_options.WatcherDebounce.TotalMilliseconds : 500))
        {
            AutoReset = false
        };

        _debounceTimer.Elapsed += (_, _) =>
        {
            try
            {
                Rescan();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Plugin rescan failed");
            }
        };

        _watcherHandler = (_, args) =>
        {
            try
            {
                if (IsRelevantPath(args.FullPath))
                    ScheduleRescan();
            }
            catch
            {
            }
        };

        _watcherRenamedHandler = (_, args) =>
        {
            try
            {
                if (IsRelevantPath(args.FullPath) || IsRelevantPath(args.OldFullPath))
                    ScheduleRescan();
            }
            catch
            {
            }
        };

        _watcher = new FileSystemWatcher(_pluginsDir)
        {
            Filter = "*.*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Changed += _watcherHandler;
        _watcher.Created += _watcherHandler;
        _watcher.Deleted += _watcherHandler;
        _watcher.Renamed += _watcherRenamedHandler;
        _watcher.EnableRaisingEvents = true;
    }

    private bool IsRelevantPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        var predicate = _options.IsRelevantPath;
        if (predicate is not null)
            return predicate(path);

        return Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private void StopWatcherLocked()
    {
        if (_watcher != null)
        {
            try { _watcher.EnableRaisingEvents = false; } catch { }

            if (_watcherHandler is not null)
            {
                _watcher.Changed -= _watcherHandler;
                _watcher.Created -= _watcherHandler;
                _watcher.Deleted -= _watcherHandler;
            }

            if (_watcherRenamedHandler is not null)
                _watcher.Renamed -= _watcherRenamedHandler;

            _watcher.Dispose();
            _watcher = null;
        }

        if (_debounceTimer != null)
        {
            try { _debounceTimer.Stop(); } catch { }
            _debounceTimer.Dispose();
            _debounceTimer = null;
        }

        _watcherHandler = null;
        _watcherRenamedHandler = null;
    }

    private void ScheduleRescan()
    {
        try
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
        catch
        {
        }
    }

    private void RaiseEvents(List<PendingEvent> pending)
    {
        if (pending.Count == 0)
            return;

        foreach (var ev in pending)
        {
            switch (ev.Kind)
            {
                case PluginChangeKind.Loaded:
                    if (ev.Current is not null)
                        PluginLoaded?.Invoke(this, new PluginLoadedEventArgs(ev.Current));
                    break;
                case PluginChangeKind.Reloaded:
                    if (ev.Current is not null)
                        PluginReloaded?.Invoke(this, new PluginReloadedEventArgs(ev.Current, ev.Previous));
                    break;
                case PluginChangeKind.Unloaded:
                    if (ev.Previous is not null)
                        PluginUnloaded?.Invoke(this, new PluginUnloadedEventArgs(ev.Previous));
                    break;
            }
        }
    }
}
