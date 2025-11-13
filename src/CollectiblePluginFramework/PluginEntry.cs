using System.Reflection;

namespace CollectiblePluginFramework;

public sealed partial class CollectiblePluginCatalog
{
    private sealed record PluginEntry(
        string Path,
        DateTime LastWriteUtc,
        PluginLoadContext Context,
        Assembly Assembly,
        WeakReference WeakRef)
    {
        public PluginSnapshot CreateSnapshot()
            => new(Path, System.IO.Path.GetFileName(Path), LastWriteUtc, Assembly, Context);
    }
}
