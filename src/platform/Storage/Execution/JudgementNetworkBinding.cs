using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class JudgementNetworkBinding
{
    internal static ExecutionPlanJudgementNetwork Capture(StateDatabase database, ProxyPreferences proxy)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        var reference = SecretFieldRows.ReadField(connection, null, SecretFieldKey.HostProxyPassword(database.InstanceId));
        return new() { ProxyMode = proxy.Mode.ToString().ToLowerInvariant(), ProxyUrl = proxy.Url,
            ProxyUserName = proxy.UserName, PasswordRevision = reference.Revision, PasswordSecretId = reference.SecretId?.ToString("D") };
    }

    internal static ProxyPreferences Proxy(ExecutionPlanJudgementNetwork wire)
    {
        var mode = wire.ProxyMode switch { "none" => ProxyMode.None, "system" => ProxyMode.System,
            "http" => ProxyMode.Http, _ => throw new IOException("execution_proxy_mode_invalid") };
        var proxy = new ProxyPreferences(mode, wire.ProxyUrl, wire.ProxyUserName);
        try { HostSettingsPolicy.Validate(HostSettings.Default with { Proxy = proxy }); }
        catch (HostSettingsException error) { throw new IOException("execution_proxy_settings_invalid", error); }
        _ = Reference(wire);
        return proxy;
    }

    internal static SecretFieldReference Reference(ExecutionPlanJudgementNetwork wire)
    {
        if (wire.PasswordSecretId is { } id) ExecutionPlanSources.RequireCanonicalId(id);
        return new(wire.PasswordRevision, wire.PasswordSecretId is null ? null : Guid.ParseExact(wire.PasswordSecretId, "D"));
    }

    internal static void Validate(ExecutionPlan plan)
    {
        if (plan.Steps.Any(step => step.JudgementWorker is not null) != (plan.JudgementNetwork is not null))
            throw new IOException("execution_judgement_network_unbound");
        if (plan.JudgementNetwork is { } network) _ = Proxy(network);
    }

    internal static void RequireCurrent(SqliteConnection connection, SqliteTransaction transaction, ExecutionPlan plan)
    {
        Validate(plan);
        if (plan.JudgementNetwork is not { } network) return;
        var settings = HostSettingsRows.Read(connection, transaction, plan.SettingsRevision);
        if (settings.Values.Proxy != Proxy(network)) throw new IOException("execution_proxy_settings_unbound");
        SecretFieldRows.RequireReference(connection, transaction,
            SecretFieldKey.HostProxyPassword(Guid.ParseExact(plan.InstanceId, "D")), Reference(network));
    }
}
