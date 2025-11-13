namespace CollectiblePluginFramework;

public sealed class PluginUnloadedEventArgs(PluginSnapshot plugin) : EventArgs
{
    public PluginSnapshot Plugin { get; } = plugin ?? throw new ArgumentNullException(nameof(plugin));
}
