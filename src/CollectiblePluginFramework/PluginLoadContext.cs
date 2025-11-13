using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace CollectiblePluginFramework;

internal sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext($"PLC:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Plugin loading requires preserved metadata and relies on dynamic assembly loading.")]
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        try
        {
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
                return null;
            if (!File.Exists(path))
                return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;

            Stream? pdbStream = null;
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (!string.IsNullOrWhiteSpace(pdbPath) && File.Exists(pdbPath))
                pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));

            try
            {
                return LoadFromStream(ms, pdbStream);
            }
            finally
            {
                pdbStream?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        try
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
                return 0;

            return LoadUnmanagedDllFromPath(path);
        }
        catch
        {
            return 0;
        }
    }
}
