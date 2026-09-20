using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Updates;

/// <summary>记录一次自动更新目标，在失败后提供有限冷却期以阻止快速重启循环。</summary>
internal sealed class StartupUpdateAttemptStore
{
    internal static readonly TimeSpan RetryCooldown = TimeSpan.FromHours(12);

    private readonly string _path;

    internal StartupUpdateAttemptStore(string? path = null)
    {
        _path = path ?? AppPaths.StartupUpdateAttemptPath;
    }

    internal string? ReadTargetVersion() => Read()?.TargetVersion;

    internal void Mark(string targetVersion, DateTimeOffset? attemptedAt = null)
    {
        if (!UpdateCatalog.TryParseTag("v" + targetVersion, out _))
        {
            throw new InvalidDataException("启动更新标记的目标版本无效");
        }
        JsonUtil.WriteAtomic(_path, JsonSerializer.Serialize(new StartupUpdateAttempt(
            targetVersion,
            attemptedAt ?? DateTimeOffset.UtcNow)));
    }

    internal void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新自动化] 清理启动更新标记失败：{ex.Message}");
        }
    }

    internal bool ShouldSuppressAutomaticTarget(string targetVersion, DateTimeOffset? now = null)
    {
        StartupUpdateAttempt? attempt = Read();
        if (attempt is null)
        {
            return false;
        }
        if (!string.Equals(attempt.TargetVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            Clear();
            return false;
        }
        DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;
        if (currentTime - attempt.AttemptedAt < RetryCooldown)
        {
            return true;
        }
        Clear();
        return false;
    }

    internal static void ClearPersisted()
    {
        new StartupUpdateAttemptStore().Clear();
    }

    private StartupUpdateAttempt? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            StartupUpdateAttempt? attempt = JsonSerializer.Deserialize<StartupUpdateAttempt>(File.ReadAllText(_path));
            if (attempt is not null
                && UpdateCatalog.TryParseTag("v" + attempt.TargetVersion, out _)
                && attempt.AttemptedAt != DateTimeOffset.MinValue)
            {
                return attempt;
            }
            Clear();
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新自动化] 读取启动更新标记失败，按无标记继续：{ex.Message}");
            Clear();
            return null;
        }
    }

    private sealed record StartupUpdateAttempt(string TargetVersion, DateTimeOffset AttemptedAt);
}
