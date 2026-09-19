using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Common;

namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>串行化插件安装、更新和卸载操作。</summary>
internal sealed class PluginRepositoryOperations
{
    private readonly Func<IReadOnlyList<PluginSummary>> _installed;
    private readonly PluginPackageService _packages;
    private readonly Func<string, CancellationToken, Task<PluginCatalogEntry>> _requireEntryAsync;
    private readonly Action _invalidateManagementSnapshot;
    private readonly Func<IReadOnlyDictionary<string, PluginOwnership>> _ownership;
    private readonly Func<string, bool> _hasPending;
    private readonly Func<string, PluginPendingOperation?> _pending;
    private readonly Func<PluginCatalogEntry, string, CancellationToken, Task<PluginPendingOperation>> _stage;
    private readonly PluginRepositorySourceContext _sourceContext;
    private readonly string _hostVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal PluginRepositoryOperations(
        Func<IReadOnlyList<PluginSummary>> installed,
        PluginPackageService packages,
        Func<string, CancellationToken, Task<PluginCatalogEntry>> requireEntryAsync,
        Action invalidateManagementSnapshot,
        Func<IReadOnlyDictionary<string, PluginOwnership>>? ownership = null,
        Func<string, bool>? hasPending = null,
        Func<PluginCatalogEntry, string, CancellationToken, Task<PluginPendingOperation>>? stage = null,
        PluginRepositorySourceContext? sourceContext = null,
        Func<string, PluginPendingOperation?>? pending = null,
        HostVersionInfo? hostVersion = null)
    {
        _installed = installed;
        _packages = packages;
        _requireEntryAsync = requireEntryAsync;
        _invalidateManagementSnapshot = invalidateManagementSnapshot;
        _ownership = ownership ?? (() => PluginInstallRecovery.ReadOwnership());
        _hasPending = hasPending ?? HasPending;
        _pending = pending ?? (name => PluginInstallRecovery.ReadPending()
            .LastOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)));
        _stage = stage ?? ((entry, action, cancellationToken) =>
            _packages.StageAsync(entry, action, cancellationToken));
        _sourceContext = sourceContext ?? PluginRepositorySourceContext.Stable;
        _hostVersion = (hostVersion ?? HostVersionInfo.Current).CurrentVersion;
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
            return await InstallEntryCoreAsync(entry, update, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按已冻结候选暂存，期间不按名称重新读取 catalog。</summary>
    internal async Task<PluginPendingOperation> StageCandidateAsync(
        PluginPackageCandidate candidate,
        bool update,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(candidate.Channel, _sourceContext.Channel, StringComparison.Ordinal)
                || candidate.FrozenEntry is null
                || !Matches(candidate, candidate.FrozenEntry))
            {
                throw new PluginRepositoryException("candidate_invalid", "插件候选已失效或与当前通道不一致");
            }
            return await InstallEntryCoreAsync(
                candidate.FrozenEntry,
                update,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PluginPendingOperation> InstallEntryCoreAsync(
        PluginCatalogEntry entry,
        bool update,
        CancellationToken cancellationToken)
    {
        PluginSummary? installed = _installed().FirstOrDefault(item =>
            string.Equals(item.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        PluginOwnership? ownership = _ownership()
            .FirstOrDefault(pair => string.Equals(pair.Key, entry.Name, StringComparison.OrdinalIgnoreCase))
            .Value;
        PluginPendingOperation? pending = _pending(entry.Name);
        if (pending is null && _hasPending(entry.Name))
        {
            pending = new PluginPendingOperation { Name = entry.Name, Action = "pending" };
        }

        if (update && installed is null)
        {
            throw new PluginRepositoryException("not_installed", $"插件尚未安装：{entry.Name}");
        }
        if (update && ownership is null)
        {
            throw new PluginRepositoryException("not_owned", $"插件安装归属未验证，拒绝更新：{entry.Name}");
        }
        if (!update && installed is not null)
        {
            throw new PluginRepositoryException("already_installed", $"插件已安装：{entry.Name}");
        }
        if (!update
            && ownership is not null
            && !PluginStoreProjector.IsCatalogArtifactMatch(ownership.ArtifactName, entry.ArtifactName))
        {
            throw new PluginRepositoryException(
                "artifact_mismatch",
                $"插件「{entry.Name}」的已验证归属与官方 catalog artifactName 不一致，拒绝替换目录");
        }
        if (pending is not null)
        {
            throw new PluginRepositoryException("pending", $"插件已有待处理操作：{entry.Name}");
        }

        PluginUpdateDecision decision = PluginUpdatePolicy.Evaluate(
            installed,
            ownership,
            entry,
            _sourceContext,
            pending,
            _hostVersion);
        if (!decision.CanStage || update && decision.Kind == PluginUpdateDecisionKind.Install)
        {
            string code = update && decision.Kind == PluginUpdateDecisionKind.NoChange
                ? "up_to_date"
                : decision.ReasonCode;
            throw new PluginRepositoryException(code, decision.Reason);
        }

        PluginPendingOperation operation = await _stage(
            entry,
            update ? "update" : "install",
            cancellationToken).ConfigureAwait(false);
        _invalidateManagementSnapshot();
        return operation;
    }

    private static bool Matches(PluginPackageCandidate candidate, PluginCatalogEntry entry)
    {
        return string.Equals(candidate.Name, entry.Name, StringComparison.Ordinal)
            && string.Equals(candidate.ArtifactName, entry.ArtifactName, StringComparison.Ordinal)
            && string.Equals(candidate.Version, entry.Version, StringComparison.Ordinal)
            && string.Equals(candidate.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
            && candidate.SizeBytes == entry.SizeBytes
            && string.Equals(candidate.PackageUrl, entry.PackageUrl, StringComparison.Ordinal)
            && string.Equals(candidate.Channel, entry.Channel, StringComparison.Ordinal)
            && string.Equals(candidate.SourceCommit, entry.SourceCommit, StringComparison.Ordinal);
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
                Channel = ownership.Channel,
                SourceCommit = ownership.SourceCommit,
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
