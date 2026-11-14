using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class AutomationRowWriter
{
    internal static void Write(SqliteConnection connection, SqliteTransaction transaction, AutomationCatalog before, AutomationCatalog next)
    {
        Row(connection, transaction, "DELETE FROM automation.queue_items; DELETE FROM automation.automation_paths;");
        foreach (var root in next.Roots)
            Row(connection, transaction, "INSERT INTO automation.automation_roots VALUES($0,$1,$2,$3,$4) ON CONFLICT(root_id) DO UPDATE SET revision=$1,name=$2,kind=$3,path=$4;",
                root.Id, root.Revision, root.Name, (int)root.Kind, root.Path);
        foreach (var script in next.Scripts) WriteScript(connection, transaction, script);
        foreach (var user in next.Users)
            Row(connection, transaction, "INSERT INTO automation.users VALUES($0,$1,$2,$3,$4,$5,$6) ON CONFLICT(user_id) DO UPDATE SET revision=$1,name=$2,user_order=$3,binding_overrides_json=$4,notes=$5,avatar=$6;",
                user.Id, user.Revision, user.Name, user.Order, BindingPolicyCodec.Encode(user.BindingOverrides), user.Notes, UserAvatar.Require(user.Avatar));
        Delete(connection, transaction, "bindings", "binding_id", before.Bindings.Select(value => value.Id), next.Bindings.Select(value => value.Id));
        foreach (var binding in next.Bindings)
            Row(connection, transaction, "INSERT INTO automation.bindings(binding_id,revision,user_id,automation_id,enabled,priority,pre_hook_json,post_hook_json,pre_once_after_success,post_final_only,notify_enabled,notification_task_ids_json,run_days,maximum_successful_runs_per_day) " +
                "VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) ON CONFLICT(binding_id) DO UPDATE SET " +
                "revision=$1,user_id=$2,automation_id=$3,enabled=$4,priority=$5,pre_hook_json=$6,post_hook_json=$7,pre_once_after_success=$8,post_final_only=$9,notify_enabled=$10,notification_task_ids_json=$11,run_days=$12,maximum_successful_runs_per_day=$13;",
                binding.Id, binding.Revision, binding.UserId, binding.ScriptId, binding.IsEnabled ? 1 : 0, binding.Priority,
                binding.PreHook is null ? null : JsonSerializer.Serialize(binding.PreHook),
                binding.PostHook is null ? null : JsonSerializer.Serialize(binding.PostHook), binding.PreOnceAfterSuccess ? 1 : 0, binding.PostFinalOnly ? 1 : 0,
                binding.NotifyEnabled ? 1 : 0, JsonSerializer.Serialize(binding.NotificationTaskIds), binding.RunDays, binding.MaximumSuccessfulRunsPerDay);
        foreach (var queue in next.Queues)
        {
            Row(connection, transaction, "INSERT INTO automation.queues(queue_id,revision,name,completion_action,run_on_startup,notify_enabled,notification_task_ids_json,success_dependencies_json,queue_order,schedule_enabled) VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT(queue_id) DO UPDATE SET revision=$1,name=$2,completion_action=$3,run_on_startup=$4,notify_enabled=$5,notification_task_ids_json=$6,success_dependencies_json=$7,queue_order=$8,schedule_enabled=$9;",
                queue.Id, queue.Revision, queue.Name, (int)queue.CompletionAction, queue.RunOnStartup ? 1 : 0,
                queue.NotifyEnabled ? 1 : 0, JsonSerializer.Serialize(queue.NotificationTaskIds), BindingPolicyCodec.EncodeDependencies(queue.SuccessDependencies), queue.Order, queue.ScheduleEnabled ? 1 : 0);
            for (var ordinal = 0; ordinal < queue.ScriptIds.Length; ordinal++)
                Row(connection, transaction, "INSERT INTO automation.queue_items VALUES($0,$1,$2);", queue.Id, ordinal, queue.ScriptIds[ordinal]);
        }
        foreach (var schedule in next.Schedules)
            Row(connection, transaction, "INSERT INTO automation.schedules VALUES($0,$1,$2,$3,$4,$5,$6,$7) ON CONFLICT(schedule_id) DO UPDATE SET revision=$1,queue_id=$2,enabled=$3,time_zone_id=$4,weekdays_json=$5,local_minute=$6,schedule_order=$7;",
                schedule.Id, schedule.Revision, schedule.QueueId, schedule.IsEnabled ? 1 : 0, schedule.TimeZoneId,
                JsonSerializer.Serialize(schedule.Weekdays), schedule.LocalMinute, schedule.Order);
        Delete(connection, transaction, "schedules", "schedule_id", before.Schedules.Select(value => value.Id), next.Schedules.Select(value => value.Id));
        Delete(connection, transaction, "queues", "queue_id", before.Queues.Select(value => value.Id), next.Queues.Select(value => value.Id));
        Delete(connection, transaction, "automations", "automation_id", before.Scripts.Select(value => value.Id), next.Scripts.Select(value => value.Id));
        Delete(connection, transaction, "users", "user_id", before.Users.Select(value => value.Id), next.Users.Select(value => value.Id));
        Delete(connection, transaction, "automation_roots", "root_id", before.Roots.Select(value => value.Id), next.Roots.Select(value => value.Id));
        Row(connection, transaction, "UPDATE automation.automation_catalog_state SET revision=$0 WHERE singleton=1 AND revision=$1;", next.Revision, before.Revision);
    }

    private static void WriteScript(SqliteConnection connection, SqliteTransaction transaction, AutomationScript script)
    {
        Row(connection, transaction, "INSERT INTO automation.automations VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17) ON CONFLICT(automation_id) DO UPDATE SET " +
            "revision=$1,name=$2,kind=$3,root_id=$4,extension_id=$5,arguments_json=$6,maximum_attempts=$7,stall_timeout_seconds=$8,total_timeout_seconds=$9," +
            "judgement_language=$10,success_keywords_json=$11,failure_keywords_json=$12,judgement_script=$13,update_configuration_at_finish=$14,target_json=$15,adapter_resources_json=$16,script_order=$17;",
            script.Id, script.Revision, script.Name, (int)script.Kind, script.RootId, script.ExtensionId, JsonSerializer.Serialize(script.Arguments),
            script.Limits.MaximumAttempts, script.Limits.StallTimeoutSeconds, script.Limits.TotalTimeoutSeconds, (int)script.Judgement.Language,
            JsonSerializer.Serialize(script.Judgement.SuccessKeywords), JsonSerializer.Serialize(script.Judgement.FailureKeywords), script.Judgement.Script, script.UpdateConfigurationAtFinish ? 1 : 0, AutomationTargetCodec.Encode(script.Target), AutomationAdapterCodec.Encode(script.AdapterResources), script.Order);
        var roles = new[] { new[] { script.Executable }.AsEnumerable(), script.ConfigurationPaths.AsEnumerable(), script.LogPaths.AsEnumerable() };
        for (var role = 0; role < roles.Length; role++)
        {
            var ordinal = 0;
            foreach (var path in roles[role])
                Row(connection, transaction, "INSERT INTO automation.automation_paths VALUES($0,$1,$2,$3,$4);", script.Id, role, ordinal++, path.RootId, path.RelativePath);
        }
    }

    private static void Delete(SqliteConnection connection, SqliteTransaction transaction, string table, string key,
        IEnumerable<Guid> before, IEnumerable<Guid> next)
    {
        foreach (var id in before.Except(next))
        {
            if (table == "bindings")
                Row(connection, transaction, "DELETE FROM configuration.configuration_current WHERE binding_id=$0;", id);
            Row(connection, transaction, $"DELETE FROM {Sqlite.DatabaseTables.Name(table)} WHERE {key}=$0;", id);
        }
    }

    internal static int Row(SqliteConnection connection, SqliteTransaction transaction, string sql, params object?[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var index = 0; index < values.Length; index++)
            command.Parameters.AddWithValue("$" + index, values[index] is Guid id ? id.ToString("D") : values[index] ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }
}

