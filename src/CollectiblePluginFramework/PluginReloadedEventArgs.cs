namespace CollectiblePluginFramework;

public sealed class PluginReloadedEventArgs(PluginSnapshot plugin, PluginSnapshot? previous) : EventArgs
{
    public PluginSnapshot Plugin { get; } = plugin ?? throw new ArgumentNullException(nameof(plugin));
    public PluginSnapshot? Previous { get; } = previous;
}
