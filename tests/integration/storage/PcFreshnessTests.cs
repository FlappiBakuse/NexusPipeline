using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcFreshnessTests
{
    [Fact(DisplayName = "A Shared PC Evidence Reader Rechecks Actual Roots Across History And Stage Reads And Ends With Its Owner")]
    public async Task RechecksCurrentRoots()
    {
        var fixture = new PcStageFixture(hooks: false);
        await fixture.ExecuteAsync(async () =>
        {
            await fixture.Consumer.RunNextAsync();
            var state = fixture.State; var source = fixture.Source;
            var binding = Assert.Single(TargetJobCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)).Binding;
            using var scope = new ExecutionSqlScope(source.Database);
            var targets = new TargetJobEvidence(source.Instance, source.Database, new(), scope);
            var stages = new PcStageEvidence(source.Instance, source.Database, new(), scope);
            var first = scope.Read(source.Database, connection => connection);
            Assert.Same(first, scope.Read(source.Database, connection => connection));
            void Verify()
            {
                Assert.NotEmpty(targets.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
                Assert.NotEmpty(stages.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            }
            Verify();
            var where = " WHERE journal_id='" + binding.ParentJournalId.ToString("D") + "' AND target_id='" + binding.TargetId.ToString("D") + "';";
            fixture.Base.Execute("UPDATE execution_target_job_roots SET total_plan_sha256='" + new string('0', 64) + "'" + where);
            try
            {
                Assert.Throws<IOException>(() => targets.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
                Assert.Throws<IOException>(() => stages.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            }
            finally { fixture.Base.Execute("UPDATE execution_target_job_roots SET total_plan_sha256='" + binding.TotalPlanSha256 + "'" + where); }
            Verify();
            scope.Dispose();
            Assert.Throws<ObjectDisposedException>(() => targets.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Throws<ObjectDisposedException>(() => stages.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        });
    }
}
