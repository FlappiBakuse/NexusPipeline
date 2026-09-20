namespace NexusPipeline.Modules.Plugins.Runtime;

/// <summary>缓存插件摘要和管理投影，并跟踪外部安装状态变化。</summary>
internal sealed class PluginManagementSnapshotCache
{
    private readonly object _sync = new();
    private long _revision;
    private IReadOnlyList<PluginSummary>? _summaries;
    private IReadOnlyList<PluginManagementView>? _views;
    private string? _stateFingerprint;

    internal long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    internal IReadOnlyList<PluginSummary> GetSummaries(Func<IReadOnlyList<PluginSummary>> factory)
    {
        lock (_sync)
        {
            return _summaries ??= factory();
        }
    }

    internal IReadOnlyList<PluginManagementView> GetViews(
        string stateFingerprint,
        Func<IReadOnlyList<PluginManagementView>> factory)
    {
        lock (_sync)
        {
            if (!string.Equals(_stateFingerprint, stateFingerprint, StringComparison.Ordinal))
            {
                _revision++;
                _summaries = null;
                _views = null;
                _stateFingerprint = stateFingerprint;
            }
            return _views ??= factory();
        }
    }

    internal void Invalidate()
    {
        lock (_sync)
        {
            _revision++;
            _summaries = null;
            _views = null;
            _stateFingerprint = null;
        }
    }
}
