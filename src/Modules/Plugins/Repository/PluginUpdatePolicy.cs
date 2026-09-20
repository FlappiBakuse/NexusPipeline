using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Shared.Common;
using System.Text.RegularExpressions;

namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>插件商店展示、自动更新和实际暂存共用的版本与来源判定。</summary>
internal static class PluginUpdatePolicy
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static PluginUpdateDecision Evaluate(
        PluginSummary? installed,
        PluginOwnership? ownership,
        PluginCatalogEntry entry,
        PluginRepositorySourceContext source,
        PluginPendingOperation? pending,
        string? hostVersion = null)
    {
        if (pending is not null)
        {
            return Blocked(
                PluginUpdateDecisionKind.BlockedPending,
                "blocked_pending",
                $"插件已有待处理事务：{entry.Name}");
        }

        if (!string.Equals(entry.Channel, source.Channel, StringComparison.Ordinal)
            || entry.Channel is not ("stable" or "develop"))
        {
            return Blocked(
                PluginUpdateDecisionKind.ChannelMismatch,
                "channel_mismatch",
                "插件候选来源与当前仓库通道不一致");
        }

        PluginCompatibilityResult compatibility = PluginRepositoryCatalog.EvaluateCompatibility(
            entry,
            hostVersion ?? HostVersionInfo.Current.CurrentVersion);
        if (!compatibility.Compatible)
        {
            return Blocked(
                PluginUpdateDecisionKind.Incompatible,
                compatibility.Code ?? "incompatible",
                compatibility.Reason);
        }

        if (installed is null)
        {
            return new PluginUpdateDecision(
                PluginUpdateDecisionKind.Install,
                CanStage: true,
                "install",
                "插件尚未安装");
        }

        if (ownership is null)
        {
            return Blocked(
                PluginUpdateDecisionKind.Unmanaged,
                "unmanaged",
                "插件安装归属未验证，不能由商店接管");
        }

        if (!Sha256Pattern.IsMatch(ownership.Sha256))
        {
            return Blocked(
                PluginUpdateDecisionKind.Unmanaged,
                "ownership_invalid",
                "插件安装归属缺少有效 SHA256，不能作为已验证 preview 接管");
        }

        if (!PluginStoreProjector.IsCatalogArtifactMatch(installed.ArtifactName, entry.ArtifactName)
            || !PluginStoreProjector.IsCatalogArtifactMatch(ownership.ArtifactName, entry.ArtifactName)
            || !PluginStoreProjector.IsCatalogArtifactMatch(installed.ArtifactName, ownership.ArtifactName))
        {
            return Blocked(
                PluginUpdateDecisionKind.ArtifactMismatch,
                "artifact_mismatch",
                "插件安装目录与已验证 artifactName 不一致");
        }

        if (!string.Equals(installed.Version, ownership.Version, StringComparison.Ordinal))
        {
            return Blocked(
                PluginUpdateDecisionKind.OwnershipMismatch,
                "ownership_mismatch",
                "插件实际 manifest 版本与已验证安装归属不一致");
        }

        int versionComparison = PluginRepositoryCatalog.CompareVersions(entry.Version, installed.Version);
        if (versionComparison > 0)
        {
            return new PluginUpdateDecision(
                PluginUpdateDecisionKind.Upgrade,
                CanStage: true,
                "upgrade",
                $"可从 v{installed.Version} 更新到 v{entry.Version}");
        }
        if (versionComparison < 0)
        {
            return Blocked(
                PluginUpdateDecisionKind.VersionAhead,
                "version_ahead",
                $"已安装版本 v{installed.Version} 高于候选 v{entry.Version}，禁止自动降级");
        }

        bool samePackage = !string.IsNullOrWhiteSpace(ownership.Sha256)
            && string.Equals(ownership.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);
        if (samePackage)
        {
            return Blocked(
                PluginUpdateDecisionKind.NoChange,
                "no_change",
                "版本与包 SHA256 均未变化");
        }

        if (string.Equals(entry.Channel, "develop", StringComparison.Ordinal))
        {
            return new PluginUpdateDecision(
                PluginUpdateDecisionKind.RefreshPreview,
                CanStage: true,
                "refresh_preview",
                "同版本 preview 包内容变化，可按 SHA256 刷新");
        }

        if (string.Equals(ownership.Channel, "develop", StringComparison.Ordinal))
        {
            return new PluginUpdateDecision(
                PluginUpdateDecisionKind.ReplacePreviewWithStable,
                CanStage: true,
                "replace_preview_with_stable",
                "同版本 stable 包可替换已验证 preview 安装");
        }

        return Blocked(
            PluginUpdateDecisionKind.StableContentConflict,
            "stable_content_conflict",
            "稳定通道同版本包不可变，拒绝不同 SHA256 的替换");
    }

    private static PluginUpdateDecision Blocked(
        PluginUpdateDecisionKind kind,
        string reasonCode,
        string reason) =>
        new(kind, CanStage: false, reasonCode, reason);
}

internal enum PluginUpdateDecisionKind
{
    Install,
    BlockedPending,
    Unmanaged,
    ArtifactMismatch,
    OwnershipMismatch,
    Incompatible,
    Upgrade,
    VersionAhead,
    NoChange,
    RefreshPreview,
    ReplacePreviewWithStable,
    StableContentConflict,
    ChannelMismatch,
}

internal sealed record PluginUpdateDecision(
    PluginUpdateDecisionKind Kind,
    bool CanStage,
    string ReasonCode,
    string Reason);
