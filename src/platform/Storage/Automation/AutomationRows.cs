using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class AutomationRows
{
    internal static AutomationCatalog Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        VerifyBudget(connection, transaction);
        var paths = ReadPaths(connection, transaction);
        var roots = Rows(connection, transaction, "SELECT root_id,revision,name,kind,path FROM automation.automation_roots ORDER BY root_id LIMIT 129;",
            row => new AutomationRoot(Id(row, 0), row.GetInt64(1), row.GetString(2), (AutomationRootKind)row.GetInt32(3), row.GetString(4)), 128);
        var scripts = Rows(connection, transaction, "SELECT automation_id,revision,name,kind,root_id,extension_id,arguments_json,maximum_attempts,stall_timeout_seconds,total_timeout_seconds,judgement_language,success_keywords_json,failure_keywords_json,judgement_script,update_configuration_at_finish,target_json,adapter_resources_json," +
            "script_order" + " FROM automation.automations ORDER BY script_order,automation_id LIMIT 4097;",
            row => Script(row, paths));
        var users = Rows(connection, transaction, "SELECT user_id,revision,name,user_order,binding_overrides_json," +
            "notes,avatar" + " FROM automation.users ORDER BY user_order,user_id LIMIT 4097;",
            row => new AutomationUser(Id(row, 0), row.GetInt64(1), row.GetString(2), row.GetInt32(3)) { BindingOverrides = BindingPolicyCodec.Decode(row.GetString(4)), Notes = row.GetString(5), Avatar = UserAvatar.Require(row.GetString(6)) });
        var bindings = Rows(connection, transaction, "SELECT binding_id,revision,user_id,automation_id,enabled,priority,pre_hook_json,post_hook_json,pre_once_after_success,post_final_only,notify_enabled,notification_task_ids_json,run_days,maximum_successful_runs_per_day FROM automation.bindings ORDER BY binding_id LIMIT 4097;",
            row => new AutomationBinding(Id(row, 0), row.GetInt64(1), Id(row, 2), Id(row, 3), row.GetInt64(4) == 1, row.GetInt32(5))
            {
                RunDays = row.GetInt32(12), MaximumSuccessfulRunsPerDay = row.GetInt32(13),
                PreHook = Hook(row, 6), PostHook = Hook(row, 7),
                PreOnceAfterSuccess = Flag(row, 8), PostFinalOnly = Flag(row, 9),
                NotifyEnabled = Flag(row, 10), NotificationTaskIds = NotificationIds(row, 11)
            });
        var queueItems = ReadQueueItems(connection, transaction);
        var queues = Rows(connection, transaction, "SELECT queue_id,revision,name,completion_action,run_on_startup,notify_enabled,notification_task_ids_json,success_dependencies_json,queue_order," +
            "schedule_enabled" + " FROM automation.queues ORDER BY queue_order,queue_id LIMIT 4097;",
            row => new AutomationQueue(Id(row, 0), row.GetInt64(1), row.GetString(2),
                queueItems.GetValueOrDefault(Id(row, 0), []), (AutomationCompletionAction)row.GetInt32(3)) { Order = row.GetInt32(8), ScheduleEnabled = Flag(row, 9), SuccessDependencies = BindingPolicyCodec.DecodeDependencies(row.GetString(7)), RunOnStartup = Flag(row, 4), NotifyEnabled = Flag(row, 5), NotificationTaskIds = NotificationIds(row, 6) });
        var schedules = Rows(connection, transaction, "SELECT schedule_id,revision,queue_id,enabled,time_zone_id,weekdays_json,local_minute," +
            "schedule_order" + " FROM automation.schedules ORDER BY schedule_order,schedule_id LIMIT 4097;",
            row => new AutomationSchedule(Id(row, 0), row.GetInt64(1), Id(row, 2), row.GetInt64(3) == 1, row.GetString(4), Array<int>(row, 5), row.GetInt32(6)) { Order = row.GetInt32(7) });
        var revision = Rows(connection, transaction, "SELECT revision FROM automation.automation_catalog_state WHERE singleton=1;", row => row.GetInt64(0), 1).Single();
        var result = new AutomationCatalog(revision, roots, scripts, users, bindings, queues, schedules);
        AutomationCatalogPolicy.Validate(result);
        return result;
    }

    private static bool Flag(SqliteDataReader row, int index) => row.GetInt64(index) switch
    { 0 => false, 1 => true, _ => throw new IOException("automation_hook_policy_invalid") };

    private static AutomationHook? Hook(SqliteDataReader row, int index)
    {
        if (row.IsDBNull(index)) return null;
        using var parsed = StrictJson.Parse(Encoding.UTF8.GetBytes(row.GetString(index)));
        var hook = parsed.Deserialize<AutomationHook>() ?? throw new IOException("automation_hook_persisted_invalid");
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(hook)))
            throw new IOException("automation_hook_persisted_invalid");
        return hook;
    }

    private static AutomationScript Script(SqliteDataReader row, Dictionary<(Guid Id, int Role), ImmutableArray<AutomationPath>> paths)
    {
        var id = Id(row, 0);
        if (!paths.TryGetValue((id, 0), out var executable) || executable.Length != 1) throw new IOException("automation_executable_path_unbound");
        return new(id, row.GetInt64(1), row.GetString(2), (AutomationKind)row.GetInt32(3), Id(row, 4),
            row.IsDBNull(5) ? null : row.GetString(5), executable[0], Array<string>(row, 6), paths.GetValueOrDefault((id, 1), []),
            paths.GetValueOrDefault((id, 2), []), new(row.GetInt32(7), row.GetInt32(8), row.GetInt32(9)),
            new((AutomationJudgementLanguage)row.GetInt32(10), Array<string>(row, 11), Array<string>(row, 12), row.IsDBNull(13) ? null : row.GetString(13))) { UpdateConfigurationAtFinish = row.GetBoolean(14), Target = AutomationTargetCodec.Decode(row.IsDBNull(15) ? null : row.GetString(15)),
                Order = row.GetInt32(17), AdapterResources = AutomationAdapterCodec.Decode(row.GetString(16)) };
    }

    private static Dictionary<(Guid Id, int Role), ImmutableArray<AutomationPath>> ReadPaths(SqliteConnection connection, SqliteTransaction transaction)
    {
        var paths = Rows(connection, transaction, "SELECT automation_id,role,ordinal,root_id,relative_path FROM automation.automation_paths ORDER BY automation_id,role,ordinal LIMIT 65537;",
            row => (Id: Id(row, 0), Role: row.GetInt32(1), Ordinal: row.GetInt32(2), Path: new AutomationPath(Id(row, 3), row.GetString(4))), 65536);
        var result = new Dictionary<(Guid, int), ImmutableArray<AutomationPath>>();
        foreach (var group in paths.GroupBy(value => (value.Id, value.Role)))
        {
            if (!group.Select(value => value.Ordinal).SequenceEqual(Enumerable.Range(0, group.Count())))
                throw new IOException("automation_path_order_invalid");
            result.Add(group.Key, group.Select(value => value.Path).ToImmutableArray());
        }
        return result;
    }

    private static Dictionary<Guid, ImmutableArray<Guid>> ReadQueueItems(SqliteConnection connection, SqliteTransaction transaction)
    {
        var items = Rows(connection, transaction, "SELECT queue_id,ordinal,automation_id FROM automation.queue_items ORDER BY queue_id,ordinal LIMIT 65537;",
            row => (Queue: Id(row, 0), Ordinal: row.GetInt32(1), Script: Id(row, 2)), 65536);
        var result = new Dictionary<Guid, ImmutableArray<Guid>>();
        foreach (var group in items.GroupBy(value => value.Queue))
        {
            if (!group.Select(value => value.Ordinal).SequenceEqual(Enumerable.Range(0, group.Count())))
                throw new IOException("automation_queue_order_invalid");
            result.Add(group.Key, group.Select(value => value.Script).ToImmutableArray());
        }
        return result;
    }

    private static ImmutableArray<Guid> NotificationIds(SqliteDataReader row, int index)
    {
        using var parsed = StrictJson.Parse(Encoding.UTF8.GetBytes(row.GetString(index)));
        var ids = parsed.Deserialize<ImmutableArray<Guid>>();
        NexusPipeline.Core.Notifications.NotificationRoutingPolicy.RequireSelection(ids);
        if (!parsed.RootElement.EnumerateArray().Select(item => item.GetString()).SequenceEqual(ids.Select(id => id.ToString("D"))))
            throw new IOException("notification_targets_invalid");
        return ids;
    }

    private static ImmutableArray<T> Array<T>(SqliteDataReader row, int index)
    {
        using var parsed = StrictJson.Parse(Encoding.UTF8.GetBytes(row.GetString(index)));
        return parsed.Deserialize<ImmutableArray<T>>();
    }

    private static Guid Id(SqliteDataReader row, int index)
    {
        var text = row.GetString(index);
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || value.ToString("D") != text)
            throw new IOException("automation_persisted_identity_invalid");
        return value;
    }

    private static ImmutableArray<T> Rows<T>(SqliteConnection connection, SqliteTransaction transaction, string sql,
        Func<SqliteDataReader, T> read, int maximum = 4096)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = ImmutableArray.CreateBuilder<T>();
        while (reader.Read())
        {
            if (rows.Count >= maximum) throw new IOException("automation_persisted_limit");
            rows.Add(read(reader));
        }
        return rows.ToImmutable();
    }

    private static void VerifyBudget(SqliteConnection connection, SqliteTransaction transaction)
    {
        var sources = new[]
        {
            ("automation_roots", "length(CAST(path||name AS BLOB))+64"),
            ("automations", "length(CAST(arguments_json||success_keywords_json||failure_keywords_json||coalesce(judgement_script,'')||coalesce(target_json,'')||adapter_resources_json||name AS BLOB))+256"),
            ("automation_paths", "length(CAST(relative_path AS BLOB))+80"),
            ("users", "length(CAST(name||binding_overrides_json" + "||notes||avatar" + " AS BLOB))+64"),
            ("bindings", "length(CAST(coalesce(pre_hook_json,'')||coalesce(post_hook_json,'')||notification_task_ids_json AS BLOB))+184"), ("queues", "length(CAST(name||notification_task_ids_json||success_dependencies_json AS BLOB))+72"), ("queue_items", "80"),
            ("schedules", "length(CAST(time_zone_id||weekdays_json AS BLOB))+128")
        };
        long bytes = 0;
        foreach (var (table, expression) in sources)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT coalesce(sum({expression}),0) FROM {Sqlite.DatabaseTables.Name(table)};";
            bytes = checked(bytes + Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            if (bytes > StrictJson.MaximumBytes) throw new IOException("automation_catalog_size_limit");
        }
    }
}

