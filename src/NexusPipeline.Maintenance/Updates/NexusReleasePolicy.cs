using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

/// <summary>宿主项目的 GitHub Release 分类策略，与 NexusVersion 的后缀阶段保持分离。</summary>
internal static class NexusReleasePolicy
{
    /// <summary>
    /// major 为 0 的版本仍处于项目 Pre-release 阶段；任意 beta/rc 后缀也必须是 Pre-release。
    /// </summary>
    public static bool RequiresGitHubPrerelease(NexusVersion version)
    {
        return version.Major == 0 || version.HasPrereleaseSuffix;
    }

    /// <summary>stable 渠道只显示符合正式 Release 分类的版本，其余渠道保留全部合法发布。</summary>
    public static bool IsVisibleInChannel(NexusVersion version, string channel)
    {
        return !string.Equals(channel, "stable", StringComparison.Ordinal)
            || !RequiresGitHubPrerelease(version);
    }
}
