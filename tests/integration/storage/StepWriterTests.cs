using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StepWriterTests
{
    [Fact(DisplayName = "A Real Configuration Free Step Registers Its Parent Job Before Effects And Persists Its Own Stopped Closure")]
    public async Task RegistersBeforeEffects()
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        Assert.Empty(fixture.Accepted.Prepared.Configurations);
        var prepared = fixture.Scope.VerifyReadOnly(); Assert.Equal(JournalPhase.Prepared, prepared.Journal.Phase);
        Assert.Equal(EffectGateState.Unentered, prepared.Gate.State); Assert.Null(Assert.Single(prepared.Steps).ClosedElapsedMilliseconds);
        using var output = new StreamReader(fixture.Output.StandardOutput); using var error = new StreamReader(fixture.Output.StandardError);
        var stdout = output.ReadToEndAsync(); var stderr = error.ReadToEndAsync();
        var started = await fixture.StartAsync(); fixture.Scope.EnterApplying();
        Assert.Equal(fixture.Accepted.Prepared.Attempt.Plan.ReadWire().AttemptId, started.AttemptId.ToString("D"));
        Assert.Equal(fixture.Scope.Command.ExecutablePath, started.ImagePath); Assert.Equal(fixture.Scope.Command.ExecutableSha256, started.ImageSha256);
        var running = fixture.Scope.VerifyReadOnly(); Assert.Equal(JournalPhase.Applying, running.Journal.Phase);
        Assert.Equal(EffectGateState.Entered, running.Gate.State); Assert.Equal(running.Latest.Revision, running.Journal.Revision);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        Assert.Equal("ready\n", await stdout.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal("", await stderr.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await fixture.Writer.AwaitRootAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(JournalPhase.Applying, fixture.Scope.VerifyReadOnly().Journal.Phase);
        Assert.Equal("original log\n", File.ReadAllText(source.Log)); Assert.Equal("{\"fixture\":true}", File.ReadAllText(source.Configuration));
        Assert.Throws<IOException>(() => fixture.Writer.Start());
    }

    [Fact(DisplayName = "An Unstarted Step Can Close Its Actual Empty Job At Zero Without Pretending The Parent Executed")]
    public async Task ClosesUnstartedJobs()
    {
        using var fixture = new StepWriterFixture();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: false);
        Assert.Empty(stopped.Closure.Family); Assert.Null(stopped.Closure.Root); Assert.Equal(0, stopped.Closure.RunElapsedMilliseconds);
        Assert.False(File.Exists(fixture.Marker)); Assert.Equal(EffectGateState.Unentered, fixture.Scope.VerifyReadOnly().Gate.State);
        Assert.Throws<IOException>(() => fixture.Writer.Start()); Assert.Throws<IOException>(() => fixture.Scope.EnterApplying());
        var repeated = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(repeated, stopped.WasExecuted);
        Assert.Equal(stopped.Reference, repeated.Reference);
    }

    [Theory(DisplayName = "A Second Scope Or The Unscoped Job Api Cannot Register Another Job For The Same Frozen Step")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsDuplicateScopes(bool entered)
    {
        using var fixture = new StepWriterFixture(); var source = fixture.Base.Source;
        if (entered) fixture.Scope.EnterApplying();
        var original = Assert.Single(fixture.Scope.VerifyReadOnly().Steps);
        Assert.Throws<IOException>(() => new StepWriterScope(source.Instance, source.Database, new(), source.Lease, fixture.JournalId, fixture.StepId));
        using var extra = new OwnedWorkerJob();
        Assert.Throws<IOException>(() => new JournalWriterStore(source.Database).RegisterJob(fixture.JournalId, extra));
        var state = fixture.Scope.VerifyReadOnly();
        Assert.Equal(original, Assert.Single(state.Steps));
        AttemptWriterState.VerifySql(source.Database, state.Journal, state.Latest, state.Total, state.Attempt.ExecutionPlan.Sha256, state.Gate);
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: false);
        Assert.Equal(entered ? EffectGateState.Entered : EffectGateState.Unentered, fixture.Scope.VerifyReadOnly().Gate.State);
        Assert.Equal(entered ? JournalPhase.Applying : JournalPhase.Prepared, fixture.Scope.VerifyReadOnly().Journal.Phase);
    }

    [Fact(DisplayName = "A Failed Close Projection Retries The Same Durable Milliseconds Without Reopening Start Or Losing The Actual Job")]
    public async Task ReplaysCloseProjection()
    {
        using var fixture = new StepWriterFixture(); await fixture.StartAsync();
        fixture.Base.Execute("CREATE TRIGGER fail_step_close BEFORE UPDATE OF revision ON operation_journals WHEN NEW.journal_id='" + fixture.JournalId.ToString("D") +
            "' BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        long elapsed;
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)));
            var pending = fixture.Scope.VerifyReadOnly(); elapsed = Assert.Single(pending.Steps).ClosedElapsedMilliseconds!.Value;
            Assert.True(pending.Latest.Revision > pending.Journal.Revision); Assert.Throws<IOException>(() => fixture.Writer.Start());
            await Task.Delay(100); Assert.Equal(elapsed, Assert.Single(fixture.Scope.VerifyReadOnly().Steps).ClosedElapsedMilliseconds);
        }
        finally { fixture.Base.Execute("DROP TRIGGER fail_step_close;"); }
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        Assert.Equal(elapsed, stopped.Closure.RunElapsedMilliseconds); var state = fixture.Scope.VerifyReadOnly(); Assert.Equal(state.Latest.Revision, state.Journal.Revision);
    }

    [Fact(DisplayName = "A Closure Insert Failure Reuses The Same Real Stopped Facts On Retry")]
    public async Task ReusesStoppedFacts()
    {
        using var fixture = new StepWriterFixture(); var root = await fixture.StartAsync();
        fixture.Base.Execute("CREATE TRIGGER fail_step_closure BEFORE INSERT ON journal_writer_job_closures " +
            "BEGIN SELECT RAISE(ABORT,'fixture_step_digest:' || NEW.plan_digest); END;");
        long revision; long? elapsed;
        SqliteException failure;
        try
        {
            failure = await Assert.ThrowsAsync<SqliteException>(() => fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)));
            var state = fixture.Scope.VerifyReadOnly(); var step = Assert.Single(state.Steps);
            revision = state.Journal.Revision; elapsed = step.ClosedElapsedMilliseconds;
            var source = fixture.Base.Source;
            Assert.Null(new JobClosureStore(source.Instance, source.Database, new())
                .Read(fixture.JournalId, step.WriterId, step.JobName, step.SessionId));
        }
        finally { fixture.Base.Execute("DROP TRIGGER fail_step_closure;"); }
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        Assert.Contains("fixture_step_digest:" + stopped.Reference.Sha256, failure.Message, StringComparison.Ordinal);
        Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId); Assert.Equal(root.CreatedAtUtc, stopped.Closure.Root.CreatedAtUtc);
        Assert.Equal(revision, fixture.Scope.VerifyReadOnly().Journal.Revision); Assert.Equal(elapsed, stopped.Closure.RunElapsedMilliseconds);
        var repeated = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10));
        fixture.AssertClosed(repeated, stopped.WasExecuted);
        Assert.Equal(stopped.Reference, repeated.Reference);
    }

    [Theory(DisplayName = "Damaged Typed Step Metadata Is Rejected Without Repairing The Original Journal Or Database")]
    [InlineData("version")]
    [InlineData("step")]
    [InlineData("job")]
    [InlineData("elapsed")]
    public async Task PreservesDamagedMetadata(string field)
    {
        using var fixture = new StepWriterFixture(); await fixture.StartAsync();
        var stopped = await fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)); fixture.AssertClosed(stopped, executed: true);
        var state = fixture.Scope.VerifyReadOnly(); var path = Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, "journals", fixture.JournalId.ToString("D"),
            state.Latest.Revision.ToString("D10") + ".json");
        var original = File.ReadAllBytes(path); var document = JsonNode.Parse(original)!.AsObject();
        var identity = document["participants"]!.AsArray()[^1]!["identity"]!.AsObject();
        if (field == "version") identity["version"] = 2;
        else if (field == "step") identity["stepId"] = "unfrozen-step";
        else if (field == "job") identity["jobName"] = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N");
        else identity["elapsedMilliseconds"] = stopped.Closure.RunElapsedMilliseconds!.Value + 1;
        File.WriteAllText(path, document.ToJsonString()); var before = fixture.Base.Source.Snapshot();
        try
        {
            if (field == "elapsed") Assert.Throws<IOException>(() => new JobClosureStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new())
                .Read(fixture.JournalId, stopped.Closure.WriterId, stopped.Closure.JobName, stopped.Closure.SessionId));
            else Assert.Throws<JsonException>(() => fixture.Scope.VerifyReadOnly());
            var after = fixture.Base.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        }
        finally { File.WriteAllBytes(path, original); }
    }
}
