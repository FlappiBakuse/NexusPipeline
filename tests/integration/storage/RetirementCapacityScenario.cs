using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class RetirementCapacityScenario
{
    internal static async Task<(TimeSpan Elapsed, long Allocated, long ManagedBytes, IReadOnlyList<string> QueryPlan)> MeasureAsync(int actualRuns, Action<int, string, double, long>? observe = null)
    {
        if (actualRuns is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(actualRuns));
        using var fixture = new ExecutionHooksFixture(false, accept: false);
        var source = fixture.Source;
        var records = new ExecutionRecordProjection(source.Instance, source.Database, new(), source.Lease,
            new(source.Instance, source.Database, new()));
        var retirement = new ContentRetirementService(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources, fixture.Base.Index);
        var clock = Stopwatch.StartNew(); var allocated = GC.GetTotalAllocatedBytes(); long managed = 0;
        for (var index = 0; index < actualRuns; index++)
        {
            var phaseStarted = Stopwatch.GetTimestamp(); var phaseAllocated = GC.GetTotalAllocatedBytes();
            _ = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionHooksFixture.Subject,
                source.Request(new BindingTarget(source.BindingA)), DateTimeOffset.UtcNow);
            Boundary("admission");
            var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Boundary("runtime");
            records.OnCommitted(receipt);
            Boundary("recordProjection");
            var page = Assert.IsType<ContentRetirementPage>(retirement.RetireLogsPage(Guid.NewGuid(), "retirement-capacity-fixture", receipt.Facts.RunId));
            Boundary("retirement");
            var registered = new RecoveryJournalRegistry(source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == page.CleanupJournalId);
            using var root = fixture.Base.Resources.TryAcquire(page.OperationId,
                registered.ResourceKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray()).Lease;
            Assert.NotNull(root);
            Assert.Equal(NexusPipeline.Core.Configuration.FileCleanupResult.Cleaned,
                new OwnedCleanupExecutor(source.Instance, new(), source.Database, fixture.Base.Index).Execute(page.CleanupJournalId, source.Lease, root!));
            Boundary("cleanup");
            managed = Math.Max(managed, GC.GetTotalMemory(forceFullCollection: false));

            void Boundary(string phase)
            {
                var now = Stopwatch.GetTimestamp(); var allocatedNow = GC.GetTotalAllocatedBytes();
                observe?.Invoke(index + 1, phase, Stopwatch.GetElapsedTime(phaseStarted, now).TotalMilliseconds,
                    allocatedNow - phaseAllocated);
                phaseStarted = Stopwatch.GetTimestamp(); phaseAllocated = GC.GetTotalAllocatedBytes();
            }
        }
        var plan = new List<string>();
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT r.relative_path,r.size_bytes,r.sha256 FROM record_retired_contents c " +
            "JOIN record_content_retirements r USING(operation_id) WHERE c.run_id=$run AND c.digest=$digest;";
        command.Parameters.AddWithValue("$run", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$digest", new string('0', 64));
        using var rows = command.ExecuteReader(); while (rows.Read()) plan.Add(rows.GetString(3));
        return (clock.Elapsed, GC.GetTotalAllocatedBytes() - allocated, managed, plan.AsReadOnly());
    }
}
