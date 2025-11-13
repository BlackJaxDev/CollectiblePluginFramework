namespace CollectiblePluginFramework;

public sealed class PluginLoadedEventArgs(PluginSnapshot plugin) : EventArgs
{
    public PluginSnapshot Plugin { get; } = plugin ?? throw new ArgumentNullException(nameof(plugin));
}
