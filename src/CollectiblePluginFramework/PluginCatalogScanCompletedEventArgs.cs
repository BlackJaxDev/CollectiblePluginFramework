namespace CollectiblePluginFramework;

public sealed class PluginCatalogScanCompletedEventArgs(bool changesApplied, DateTime lastScanUtc) : EventArgs
{
    public bool ChangesApplied { get; } = changesApplied;
    public DateTime LastScanUtc { get; } = lastScanUtc;
}
