using System.Reflection;
using System.Runtime.Loader;

namespace CollectiblePluginFramework;

public sealed record PluginSnapshot(
    string Path,
    string FileName,
    DateTime LastWriteUtc,
    Assembly Assembly,
    AssemblyLoadContext LoadContext);
