using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Plugins;

/// <summary>插件仓库安装、更新和卸载事务编排。</summary>
internal sealed partial class PluginRepositoryService
{
    public async Task<PluginPendingOperation> InstallAsync(
        string name,
        bool update,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginCatalogEntry entry = await RequireEntryAsync(name, cancellationToken).ConfigureAwait(false);
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
                throw new PluginRepositoryException("pending", $"插件已有待重启事务：{entry.Name}");
            }
            if (update && installed is not null
                && PluginRepositoryCatalog.CompareVersions(installed.Version, entry.Version) >= 0)
            {
                throw new PluginRepositoryException("up_to_date", $"插件已是 v{installed.Version}");
            }
            PluginPendingOperation operation = await _packages.StageAsync(
                entry,
                update ? "update" : "install",
                cancellationToken).ConfigureAwait(false);
            _plugins().InvalidateManagementSnapshot();
            return operation;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PluginPendingOperation> UninstallAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                throw new PluginRepositoryException("pending", $"插件已有待重启事务：{actualName}");
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
            _plugins().InvalidateManagementSnapshot();
            return operation;
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
