using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PcFreshnessTests(ITestOutputHelper output)
{
    [Fact(DisplayName = "Two Real Visible Targets Share One Observation And An Ended Target Invalidates The Claim")]
    public async Task GroupsVisibleTargetWindows()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "pc-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var first = new OwnedCrashProcess();
        using var second = new OwnedCrashProcess();
        var firstWindow = Path.Combine(root, "first.json");
        var secondWindow = Path.Combine(root, "second.json");
        var one = first.Start(["capture-window-fixture", firstWindow], root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "first-owner.json"));
        var two = second.Start(["capture-window-fixture", secondWindow], root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "second-owner.json"));
        Assert.Equal("windowReady", (await first.ReadReportAsync(firstWindow)).GetProperty("status").GetString());
        Assert.Equal("windowReady", (await second.ReadReportAsync(secondWindow)).GetProperty("status").GetString());
        Assert.Equal(one.ImageSha256, two.ImageSha256);
        var path = one.ImagePath;
        var target = new PcTargetImage(path, new FileInfo(path).Length, one.ImageSha256, PhysicalObjectIdentity.Read(path));
        using var ready = Measure(() => PcTargetReadiness.Capture(target, default, PcReadinessMode.VisibleWindow), out var capture);
        Assert.Equal(ExecutionTargetReadiness.Ready, ready.State);
        var processes = Measure(() => ready.Use(identities => identities.Select(identity => identity.ProcessId).Order().ToArray()), out var use);
        Assert.Equal(new[] { one.ProcessId, two.ProcessId }.Order(), processes);
        Assert.True((await first.StopAsync()).CompleteJobStopped);
        Assert.Equal("execution_target_readiness_expired", Assert.Throws<IOException>(() => ready.Use(_ => true)).Message);
        output.WriteLine($"two visible targets: capture process/windows={capture.Processes}/{capture.Windows}, use process/windows={use.Processes}/{use.Windows}");
        Assert.Equal((1L, 1L), capture);
        Assert.Equal((0L, 1L), use);
    }

    private static T Measure<T>(Func<T> action, out (long Processes, long Windows) counts)
    {
        var stderr = Console.Error;
        using var text = new StringWriter();
        T result;
        try
        {
            Console.SetError(text);
            using var profile = StartupProfile.Begin("pc-visible-readiness");
            result = action();
        }
        finally { Console.SetError(stderr); }
        using var report = JsonDocument.Parse(text.ToString());
        var steps = report.RootElement.GetProperty("steps").EnumerateArray().ToDictionary(
            step => step.GetProperty("name").GetString()!, step => step.GetProperty("count").GetInt64());
        counts = (steps.GetValueOrDefault("readiness.process-snapshot"), steps.GetValueOrDefault("readiness.window-snapshot"));
        return result;
    }

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
            void Verify()
            {
                Assert.NotEmpty(targets.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
                Assert.NotEmpty(stages.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            }
            Verify();
            var where = " WHERE journal_id='" + binding.ParentJournalId.ToString("D") + "' AND target_id='" + binding.TargetId.ToString("D") + "';";
            fixture.Base.Execute("UPDATE main.execution_target_job_roots SET total_plan_sha256='" + new string('0', 64) + "'" + where);
            try
            {
                Assert.Throws<IOException>(() => targets.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
                Assert.Throws<IOException>(() => stages.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            }
            finally { fixture.Base.Execute("UPDATE main.execution_target_job_roots SET total_plan_sha256='" + binding.TotalPlanSha256 + "'" + where); }
            Verify();
            scope.Dispose();
            Assert.Throws<ObjectDisposedException>(() => targets.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
            Assert.Throws<ObjectDisposedException>(() => stages.RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        });
    }
}
