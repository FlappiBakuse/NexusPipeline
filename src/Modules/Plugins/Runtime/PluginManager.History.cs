using System.Reflection;
using System.Text.Json;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Settings.Persistence;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Shared.Common;

namespace NexusPipeline.Modules.Plugins.Runtime;

internal sealed partial class PluginManager : IPluginCapabilityResolver, IPluginAvailability, IUserRunStartingPublisher, IEmulatorSupportProviderResolver, IPluginExecutionProviderResolver, IDisposable
{
    /// <summary>在历史提交前收集已注册插件的展示快照；插件异常或超限只会丢弃该插件的展示内容。</summary>
    internal void EnrichHistory(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.StartTime == DateTime.MinValue)
        {
            return;
        }

        var context = new PluginHistoryContext(
            record.Id,
            record.UserId,
            record.UserName,
            record.ScriptInstanceId,
            record.ScriptName,
            record.QueueId,
            record.QueueName,
            record.Mode,
            new DateTimeOffset(record.StartTime),
            record.EndTime.HasValue ? new DateTimeOffset(record.EndTime.Value) : null,
            record.Status);
        var snapshots = new List<PluginHistoryRecord>();
        int totalBytes = 0;
        foreach (PluginHistoryContributionRegistration registration in HistoryContributions)
        {
            PluginHistoryDisplay? display;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                display = registration.Contribution.Handler(context, timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献执行失败（{registration.Contribution.Id}）：{ex.Message}");
                continue;
            }
            if (!PluginUiValidation.TrySanitizeHistoryDisplay(display, out PluginHistoryDisplay? sanitized, out string error)
                || sanitized is null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献无效（{registration.Contribution.Id}）：{error}");
                }
                continue;
            }

            var snapshot = new PluginHistoryRecord
            {
                PluginName = registration.PluginName,
                PluginDisplayName = registration.PluginDisplayName,
                Id = sanitized.Id,
                Title = sanitized.Title,
                LocalizedTitle = ToLocalizedTextRecord(sanitized.LocalizedTitle),
                Order = registration.Contribution.Order,
                Badges = sanitized.Badges?.Select(badge => new PluginHistoryBadgeRecord
                {
                    Label = badge.Label,
                    Tone = badge.Tone,
                    Title = badge.Title,
                    LocalizedLabel = ToLocalizedTextRecord(badge.LocalizedLabel),
                    LocalizedTitle = ToLocalizedTextRecord(badge.LocalizedTitle),
                }).ToList() ?? new List<PluginHistoryBadgeRecord>(),
                Fields = sanitized.Fields?.Select(field => new PluginHistoryFieldRecord
                {
                    Label = field.Label,
                    Value = field.Value,
                    Tone = field.Tone,
                    LocalizedLabel = ToLocalizedTextRecord(field.LocalizedLabel),
                    LocalizedValue = ToLocalizedValueRecord(field.LocalizedValue),
                }).ToList() ?? new List<PluginHistoryFieldRecord>(),
            };
            int bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts.Default).Length;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献序列化失败（{registration.Contribution.Id}）：{ex.Message}");
                continue;
            }
            if (bytes > 16 * 1024 || totalBytes + bytes > 64 * 1024)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 历史展示贡献超出大小上限（{registration.Contribution.Id}）");
                continue;
            }
            snapshots.Add(snapshot);
            totalBytes += bytes;
        }
        record.PluginHistory = snapshots;
    }

    internal void LocalizeHistory(RunRecord record, string locale)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (PluginHistoryRecord item in record.PluginHistory ?? new List<PluginHistoryRecord>())
        {
            item.PluginDisplayName = LocalizePluginDisplayName(item.PluginName, item.PluginDisplayName, locale);
            PluginLocalizationManifest localization = GetPluginLocalization(item.PluginName);
            if (item.LocalizedTitle is not null)
            {
                item.Title = localization.Resolve(locale, item.LocalizedTitle.Key, item.Title);
            }
            foreach (PluginHistoryBadgeRecord badge in item.Badges ?? new List<PluginHistoryBadgeRecord>())
            {
                if (badge.LocalizedLabel is not null)
                {
                    badge.Label = localization.Resolve(locale, badge.LocalizedLabel.Key, badge.Label);
                }
                if (badge.LocalizedTitle is not null)
                {
                    badge.Title = localization.Resolve(locale, badge.LocalizedTitle.Key, badge.Title);
                }
            }
            foreach (PluginHistoryFieldRecord field in item.Fields ?? new List<PluginHistoryFieldRecord>())
            {
                if (field.LocalizedLabel is not null)
                {
                    field.Label = localization.Resolve(locale, field.LocalizedLabel.Key, field.Label);
                }
                if (field.LocalizedValue is not null)
                {
                    field.Value = localization.Resolve(
                        locale,
                        field.LocalizedValue.Key,
                        field.LocalizedValue.Fallback.Length > 0 ? field.LocalizedValue.Fallback : field.Value,
                        field.LocalizedValue.Args);
                }
            }
        }
    }

    private static PluginLocalizedTextRecord? ToLocalizedTextRecord(PluginLocalizedText? value)
    {
        return value is null ? null : new PluginLocalizedTextRecord { Key = value.Key, Fallback = value.Fallback };
    }

    private static PluginLocalizedValueRecord? ToLocalizedValueRecord(PluginLocalizedValue? value)
    {
        return value is null
            ? null
            : new PluginLocalizedValueRecord
            {
                Key = value.Key,
                Fallback = value.Fallback,
                Args = value.Args is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(value.Args, StringComparer.Ordinal),
            };
    }

}
