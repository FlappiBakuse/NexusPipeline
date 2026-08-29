using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed record PluginUserGlobalSettingsOptionView(
    string Value,
    string Label);

internal sealed record PluginUserGlobalSettingsFieldView(
    string Key,
    string Label,
    string Type,
    string Description,
    bool Required,
    string Placeholder,
    int MaxLength,
    bool ReadOnly,
    IReadOnlyList<PluginUserGlobalSettingsOptionView>? Options);

internal sealed record PluginUserGlobalSettingsView(
    string PluginName,
    string PluginDisplayName,
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<PluginUserGlobalSettingsFieldView> Fields,
    JsonObject Values);

/// <summary>用户全局插件设置的控制面服务；Web 与 MCP 共用读取、校验、脱敏和超时边界。</summary>
internal sealed class PluginUserGlobalSettingsService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IReadOnlyList<PluginUserGlobalManagementRegistration>> _contributions;

    public PluginUserGlobalSettingsService(PluginManager plugins)
    {
        _contributions = () => plugins.UserGlobalManagementContributions;
    }

    internal PluginUserGlobalSettingsService(
        Func<IReadOnlyList<PluginUserGlobalManagementRegistration>> contributions)
    {
        _contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
    }

    internal bool TryGetRegistration(
        string pluginName,
        string contributionId,
        out PluginUserGlobalManagementRegistration? registration)
    {
        registration = _contributions().FirstOrDefault(item =>
            string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Contribution.Id, contributionId, StringComparison.OrdinalIgnoreCase));
        return registration is not null;
    }

    public async Task<OperationResult<IReadOnlyList<PluginUserGlobalSettingsView>>> ReadAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PluginUserGlobalSettingsView>();
        foreach (PluginUserGlobalManagementRegistration registration in _contributions())
        {
            OperationResult<PluginUserGlobalSettingsView> item = await ReadRegistrationAsync(
                registration,
                userId,
                cancellationToken).ConfigureAwait(false);
            if (!item.Succeeded)
            {
                return OperationResult<IReadOnlyList<PluginUserGlobalSettingsView>>.Failure(item.Error!);
            }
            result.Add(item.Value!);
        }
        return OperationResult<IReadOnlyList<PluginUserGlobalSettingsView>>.Ok(result);
    }

    public async Task<OperationResult<PluginUserGlobalSettingsView>> ReadOneAsync(
        string userId,
        string pluginName,
        string contributionId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetRegistration(pluginName, contributionId, out PluginUserGlobalManagementRegistration? registration)
            || registration is null)
        {
            return NotFound<PluginUserGlobalSettingsView>();
        }
        return await ReadRegistrationAsync(registration, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<bool>> SaveAsync(
        string userId,
        string pluginName,
        string contributionId,
        JsonObject values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!TryGetRegistration(pluginName, contributionId, out PluginUserGlobalManagementRegistration? registration)
            || registration is null)
        {
            return NotFound<bool>();
        }
        if (!PluginContributionValidation.TryValidateSave(
                registration,
                values,
                out JsonObject sanitized,
                out string validationError))
        {
            return OperationResult<bool>.Failure(
                "validation_error",
                validationError,
                OperationErrorKind.Validation);
        }

        try
        {
            using CancellationTokenSource timeout = CreateTimeout(cancellationToken, SaveTimeout);
            await registration.Contribution.SaveHandler(
                    userId,
                    sanitized,
                    timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            return OperationResult<bool>.Ok(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return OperationResult<bool>.Failure(
                "plugin_timeout",
                "保存插件设置超时",
                OperationErrorKind.Timeout);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] 用户全局设置保存失败：{ex.Message}");
            return OperationResult<bool>.Failure(
                "plugin_error",
                "保存插件设置失败",
                OperationErrorKind.Internal);
        }
    }

    private async Task<OperationResult<PluginUserGlobalSettingsView>> ReadRegistrationAsync(
        PluginUserGlobalManagementRegistration registration,
        string userId,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CreateTimeout(cancellationToken, ReadTimeout);
            JsonObject values = PluginContributionValidation.SanitizeRead(
                registration,
                await registration.Contribution.ReadHandler(userId, timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false));
            return OperationResult<PluginUserGlobalSettingsView>.Ok(Project(registration, values));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return OperationResult<PluginUserGlobalSettingsView>.Failure(
                "plugin_timeout",
                "读取插件设置超时",
                OperationErrorKind.Timeout);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] 用户全局设置读取失败：{ex.Message}");
            return OperationResult<PluginUserGlobalSettingsView>.Failure(
                "plugin_error",
                "读取插件设置失败",
                OperationErrorKind.Internal);
        }
    }

    private static PluginUserGlobalSettingsView Project(
        PluginUserGlobalManagementRegistration registration,
        JsonObject values)
    {
        return new PluginUserGlobalSettingsView(
            registration.PluginName,
            registration.PluginDisplayName,
            registration.Contribution.Id,
            registration.Contribution.Title,
            registration.Contribution.Description,
            registration.Contribution.Order,
            registration.Contribution.Fields.Select(field => new PluginUserGlobalSettingsFieldView(
                field.Key,
                field.Label,
                field.Type,
                field.Description,
                field.Required,
                field.Placeholder,
                field.MaxLength,
                field.ReadOnly || field.Type.Equals("status", StringComparison.OrdinalIgnoreCase),
                field.Options?.Select(option => new PluginUserGlobalSettingsOptionView(
                    option.Value,
                    option.Label)).ToArray())).ToArray(),
            values);
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static OperationResult<T> NotFound<T>() => OperationResult<T>.Failure(
        "contribution_not_found",
        "插件设置贡献不存在或插件未启用",
        OperationErrorKind.NotFound);
}
