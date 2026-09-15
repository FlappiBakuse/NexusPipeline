using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Plugins;

/// <summary>串行化插件安装、更新和卸载操作。</summary>
internal sealed class PluginRepositoryOperations
{
    private readonly Func<PluginManager> _plugins;
    private readonly PluginPackageService _packages;
    private readonly Func<string, CancellationToken, Task<PluginCatalogEntry>> _requireEntryAsync;
    private readonly Action _invalidateManagementSnapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal PluginRepositoryOperations(
        Func<PluginManager> plugins,
        PluginPackageService packages,
        Func<string, CancellationToken, Task<PluginCatalogEntry>> requireEntryAsync,
        Action invalidateManagementSnapshot)
    {
        _plugins = plugins;
        _packages = packages;
        _requireEntryAsync = requireEntryAsync;
        _invalidateManagementSnapshot = invalidateManagementSnapshot;
    }

    internal async Task<PluginPendingOperation> InstallAsync(
        string name,
        bool update,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginCatalogEntry entry = await _requireEntryAsync(name, cancellationToken).ConfigureAwait(false);
            PluginCompatibilityResult compatibility = PluginRepositoryCatalog.EvaluateCompatibility(
                entry,
                UpdateService.CurrentVersion);
            if (!compatibility.Compatible)
            {
                throw new PluginRepositoryException(
                    compatibility.Code ?? "incompatible",
                    compatibility.Reason);
            }
            PluginSummary? installed = _plugins().PluginSummaries.FirstOrDefault(item =>
                string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (update && installed is null)
            {
                throw new PluginRepositoryException("not_installed", $"插件尚未安装：{entry.Name}");
            }
            if (!update && installed is not null)
            {
                throw new PluginRepositoryException("already_installed", $"插件已安装：{entry.Name}");
            }
            if (HasPending(entry.Name))
            {
                throw new PluginRepositoryException("pending", $"插件已有待处理操作：{entry.Name}");
            }
            if (update && installed is not null
                && PluginRepositoryCatalog.CompareVersions(installed.Version, entry.Version) >= 0)
            {
                throw new PluginRepositoryException("up_to_date", $"当前版本已是最新：v{installed.Version}");
            }
            PluginPendingOperation operation = await _packages.StageAsync(
                entry,
                update ? "update" : "install",
                cancellationToken).ConfigureAwait(false);
            _invalidateManagementSnapshot();
            return operation;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PluginPendingOperation> UninstallAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PluginRepositoryCatalog.IsCanonicalPluginId(name))
            {
                throw new PluginRepositoryException("invalid_name", "插件名称无效");
            }
            PluginSummary? installed = _plugins().PluginSummaries.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            PluginOwnership? ownership = PluginInstallRecovery.ReadOwnership()
                .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (installed is null && ownership is null)
            {
                throw new PluginRepositoryException("not_installed", $"插件尚未安装：{name}");
            }
            string actualName = installed?.Name ?? ownership!.Name;
            if (HasPending(actualName))
            {
                throw new PluginRepositoryException("pending", $"插件已有待处理操作：{actualName}");
            }
            var operation = new PluginPendingOperation
            {
                Action = "uninstall",
                Name = actualName,
                ArtifactName = installed?.ArtifactName ?? ownership!.ArtifactName,
                Version = installed?.Version ?? ownership!.Version,
                Kind = installed?.Kind ?? ownership!.Kind,
                ApiVersion = installed?.ApiVersion ?? ownership!.ApiVersion,
                Phase = "pending",
                StagedPath = Path.Combine(AppPaths.PluginStagingDir, $"uninstall.{actualName}.{Guid.NewGuid():N}"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            PluginInstallRecovery.AddPending(operation);
            _invalidateManagementSnapshot();
            return operation;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HasPending(string name)
    {
        return PluginInstallRecovery.ReadPending()
            .Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
