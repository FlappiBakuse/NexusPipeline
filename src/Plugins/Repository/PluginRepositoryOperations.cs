using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;

namespace NexusPipeline.Plugins;

/// <summary>串行化插件安装、更新和卸载操作。</summary>
internal sealed class PluginRepositoryOperations
{
    private readonly Func<IReadOnlyList<PluginSummary>> _installed;
    private readonly PluginPackageService _packages;
    private readonly Func<string, CancellationToken, Task<PluginCatalogEntry>> _requireEntryAsync;
    private readonly Action _invalidateManagementSnapshot;
    private readonly Func<IReadOnlyDictionary<string, PluginOwnership>> _ownership;
    private readonly Func<string, bool> _hasPending;
    private readonly Func<PluginCatalogEntry, string, CancellationToken, Task<PluginPendingOperation>> _stage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal PluginRepositoryOperations(
        Func<IReadOnlyList<PluginSummary>> installed,
        PluginPackageService packages,
        Func<string, CancellationToken, Task<PluginCatalogEntry>> requireEntryAsync,
        Action invalidateManagementSnapshot,
        Func<IReadOnlyDictionary<string, PluginOwnership>>? ownership = null,
        Func<string, bool>? hasPending = null,
        Func<PluginCatalogEntry, string, CancellationToken, Task<PluginPendingOperation>>? stage = null)
    {
        _installed = installed;
        _packages = packages;
        _requireEntryAsync = requireEntryAsync;
        _invalidateManagementSnapshot = invalidateManagementSnapshot;
        _ownership = ownership ?? (() => PluginInstallRecovery.ReadOwnership());
        _hasPending = hasPending ?? HasPending;
        _stage = stage ?? ((entry, action, cancellationToken) =>
            _packages.StageAsync(entry, action, cancellationToken));
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
            PluginSummary? installed = _installed().FirstOrDefault(item =>
                string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (update && installed is null)
            {
                throw new PluginRepositoryException("not_installed", $"插件尚未安装：{entry.Name}");
            }
            PluginOwnership? ownership = _ownership()
                .GetValueOrDefault(entry.Name);
            if (update && ownership is null)
            {
                throw new PluginRepositoryException("not_owned", $"插件安装归属未验证，拒绝更新：{entry.Name}");
            }
            if (!update
                && ownership is not null
                && !PluginStoreProjector.IsCatalogArtifactMatch(ownership.ArtifactName, entry.ArtifactName))
            {
                throw new PluginRepositoryException(
                    "artifact_mismatch",
                    $"插件「{entry.Name}」的已验证归属与官方 catalog artifactName 不一致，拒绝替换目录");
            }
            if (update && installed is not null
                && !PluginStoreProjector.IsCatalogArtifactMatch(installed.ArtifactName, entry.ArtifactName))
            {
                throw new PluginRepositoryException(
                    "artifact_mismatch",
                    $"插件「{entry.Name}」的安装目录与官方 catalog artifactName 不一致，拒绝替换目录");
            }
            if (!update && installed is not null)
            {
                throw new PluginRepositoryException("already_installed", $"插件已安装：{entry.Name}");
            }
            if (_hasPending(entry.Name))
            {
                throw new PluginRepositoryException("pending", $"插件已有待处理操作：{entry.Name}");
            }
            if (update && installed is not null
                && PluginRepositoryCatalog.CompareVersions(installed.Version, entry.Version) >= 0)
            {
                throw new PluginRepositoryException("up_to_date", $"当前版本已是最新：v{installed.Version}");
            }
            PluginPendingOperation operation = await _stage(
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
            PluginSummary? installed = _installed().FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            PluginOwnership? ownership = _ownership()
                .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (ownership is null)
            {
                throw new PluginRepositoryException("not_owned", $"插件安装归属未验证，拒绝卸载：{name}");
            }
            if (installed is not null
                && !PluginStoreProjector.IsCatalogArtifactMatch(installed.ArtifactName, ownership.ArtifactName))
            {
                throw new PluginRepositoryException(
                    "artifact_mismatch",
                    $"插件「{name}」的安装目录与已验证归属不一致，拒绝删除目录");
            }
            string actualName = ownership.Name;
            if (_hasPending(actualName))
            {
                throw new PluginRepositoryException("pending", $"插件已有待处理操作：{actualName}");
            }
            var operation = new PluginPendingOperation
            {
                Action = "uninstall",
                Name = actualName,
                ArtifactName = installed?.ArtifactName ?? ownership.ArtifactName,
                Version = installed?.Version ?? ownership.Version,
                Kind = installed?.Kind ?? ownership.Kind,
                ApiVersion = installed?.ApiVersion ?? ownership.ApiVersion,
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
