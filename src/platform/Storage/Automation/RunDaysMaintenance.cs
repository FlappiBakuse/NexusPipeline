using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Automation;

internal sealed class RunDaysMaintenance(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, TimeProvider clock)
{
    private bool _observed;
    private DateOnly? _lastObservedDay;

    internal void Tick()
    {
        lease.VerifyHeld(instance);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.Local).DateTime);
        if (_lastObservedDay is { } observed && today <= observed) return;
        var admission = resources.TryAcquire(Guid.NewGuid(), [new("automation:definitions", ResourceClaimMode.Mutation)]);
        if (admission.Lease is null) return;
        using var held = admission.Lease;
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT local_day FROM automation.automation_run_days_state WHERE singleton=1;";
            var prior = command.ExecuteScalar() is long day ? DateOnly.FromDayNumber(checked((int)day)) : (DateOnly?)null;
            if (prior is { } saved && saved >= today) return false;
            // A new host begins at today's date; missed offline days are never caught up.
            if (_observed && prior is not null)
            {
                var before = AutomationRows.Read(connection, transaction);
                var users = before.Users.Select(user => user.BindingOverrides.General is { SyncEnabled: true, RunDays: > 0 } general
                    ? user with { Revision = checked(user.Revision + 1), BindingOverrides = user.BindingOverrides with
                        { General = general with { RunDays = general.RunDays - 1 } } } : user).ToImmutableArray();
                var synchronized = before.Users.Where(user => user.BindingOverrides.General.SyncEnabled).Select(user => user.Id).ToHashSet();
                var bindings = before.Bindings.Select(binding => !synchronized.Contains(binding.UserId) && binding.RunDays > 0
                    ? binding with { Revision = checked(binding.Revision + 1), RunDays = binding.RunDays - 1 } : binding).ToImmutableArray();
                if (!users.SequenceEqual(before.Users) || !bindings.SequenceEqual(before.Bindings))
                    AutomationRowWriter.Write(connection, transaction, before, before with
                        { Revision = checked(before.Revision + 1), Users = users, Bindings = bindings });
            }
            command.CommandText = "INSERT INTO automation.automation_run_days_state VALUES(1,$day) ON CONFLICT(singleton) DO UPDATE SET local_day=$day;";
            command.Parameters.AddWithValue("$day", today.DayNumber); command.ExecuteNonQuery();
            return true;
        }));
        _observed = true;
        _lastObservedDay = today;
    }
}
