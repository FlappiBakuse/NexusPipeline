using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class HostSettingsRows
{
    private const string Columns = "revision,auto_start,desktop_mode,minimize_to_tray,open_desktop,web_port,remote_access," +
        "mcp_enabled,mcp_write,mcp_port,config_repair,retention_days,log_level,locale,proxy_mode,proxy_url,proxy_user," +
        "update_check,update_auto,plugin_auto,update_channel,update_source,plugin_source,retry_enabled,retry_backoff_seconds";

    internal static HostSettingsSnapshot ReadCurrent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM settings_current WHERE singleton=1;";
        var value = command.ExecuteScalar() ?? throw new IOException("settings_current_missing");
        return Read(connection, transaction, Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    internal static HostSettingsSnapshot Read(SqliteConnection connection, SqliteTransaction transaction, long revision)
    {
        HostSettings value;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {Columns} FROM settings_snapshots WHERE revision=$revision;";
            command.Parameters.AddWithValue("$revision", revision);
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new IOException("settings_snapshot_missing");
            value = new(row.GetBoolean(1), new((DesktopMode)row.GetInt32(2), row.GetBoolean(3), row.GetBoolean(4)),
                new(row.GetInt32(5), row.GetBoolean(6), row.GetBoolean(7), row.GetBoolean(8), row.GetInt32(9)),
                row.GetBoolean(10), row.GetInt32(11), (HostLogLevel)row.GetInt32(12), (HostLocale)row.GetInt32(13),
                new((ProxyMode)row.GetInt32(14), Optional(row, 15), row.GetString(16)),
                new(row.GetBoolean(17), row.GetBoolean(18), row.GetBoolean(19), (UpdateChannel)row.GetInt32(20),
                    Optional(row, 21), (PluginSourceChannel)row.GetInt32(22)), [])
                { AutomaticRetry = new(row.GetBoolean(23), row.GetInt32(24)) };
        }
        var extensions = ImmutableArray.CreateBuilder<ExtensionPreference>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT extension_id,enabled FROM settings_extensions WHERE revision=$revision ORDER BY extension_id LIMIT 257;";
            command.Parameters.AddWithValue("$revision", revision);
            using var rows = command.ExecuteReader();
            while (rows.Read()) extensions.Add(new(rows.GetString(0), rows.GetBoolean(1)));
        }
        value = value with { Extensions = extensions.ToImmutable() };
        HostSettingsPolicy.Validate(value);
        return new(revision, value);
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, HostSettingsSnapshot snapshot)
    {
        HostSettingsPolicy.Validate(snapshot.Values);
        var v = snapshot.Values;
        object?[] values = [snapshot.Revision, v.AutoStart, v.Desktop.Mode, v.Desktop.MinimizeToTray, v.Desktop.OpenOnStartup,
            v.Service.WebPort, v.Service.AllowRemoteAccess, v.Service.McpEnabled, v.Service.McpWriteEnabled, v.Service.McpPort,
            v.AllowConfigRepair, v.HistoryRetentionDays, v.LogLevel, v.Locale, v.Proxy.Mode, v.Proxy.Url, v.Proxy.UserName,
            v.Updates.CheckEnabled, v.Updates.AutoApplyEnabled, v.Updates.PluginAutoUpdateEnabled, v.Updates.Channel,
            v.Updates.SourceUrl, v.Updates.PluginSource, v.AutomaticRetry.Enabled, v.AutomaticRetry.BackoffSeconds];
        Execute(connection, transaction, $"INSERT INTO settings_snapshots({Columns}) VALUES(" +
            string.Join(',', Enumerable.Range(0, values.Length).Select(index => "$" + index)) + ");", values);
        foreach (var item in v.Extensions)
            Execute(connection, transaction, "INSERT INTO settings_extensions VALUES($0,$1,$2);", snapshot.Revision, item.ExtensionId, item.Enabled);
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object?[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is Enum) value = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is Guid id) value = id.ToString("D");
            command.Parameters.AddWithValue("$" + i, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private static string? Optional(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
}
