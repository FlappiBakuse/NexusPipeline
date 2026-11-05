using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WriterBoundaryTests
{
    [Fact(DisplayName = "A Second Run Job Is Rejected While The Original Admission And Registered Job Remain Open")]
    public async Task RejectsSecondJobs()
    {
        await using var fixture = new RunWriterFixture(install: false);
        var count = fixture.Base.Count("journal_writer_jobs");
        Assert.Equal(2, count);
        Assert.Equal("writer_job_registration_unbound", Assert.Throws<IOException>(() => new RunWriterProcess(
            fixture.Base.Source.Instance, fixture.Base.Source.Database, new(), fixture.Base.Lease,
            fixture.Session.SessionId, ConfigurationRunFixture.Subject)).Message);
        Assert.Equal(count, fixture.Base.Count("journal_writer_jobs"));
        Assert.False(Current(fixture).IsWriterAdmissionClosed);
        var stopped = await fixture.StopAsync(); Assert.False(stopped.WasExecuted);
        Assert.Equal(0, Current(fixture).ClosedElapsedMilliseconds);
    }

    [Fact(DisplayName = "A Failed First Run Gate Write Freezes The Original Clock And Cannot Gain Eligibility By Waiting")]
    public async Task FreezesFailedStart()
    {
        await using var fixture = new RunWriterFixture(); await fixture.StartAsync();
        fixture.Base.Execute("CREATE TRIGGER fixture_fail_run_gate BEFORE UPDATE OF writer_gate ON configuration_run_sessions " +
            "WHEN NEW.writer_gate=1 BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        TimeSpan frozen;
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Writer.StopAsync(TimeSpan.FromSeconds(10)));
            frozen = fixture.Writer.ElapsedSinceStart;
            Assert.InRange(frozen.TotalMilliseconds, 0, 14999);
            Assert.False(Current(fixture).IsWriterAdmissionClosed);
            Assert.Throws<IOException>(() => fixture.Writer.Start());
            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.Equal(frozen, fixture.Writer.ElapsedSinceStart);
        }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_fail_run_gate;"); }
        var stopped = await fixture.StopAsync();
        Assert.Equal(frozen.Ticks / TimeSpan.TicksPerMillisecond, Current(fixture).ClosedElapsedMilliseconds);
        fixture.Seal(stopped);
        using (var sample = new RunProgressCapture(fixture.Base.Source.Instance, fixture.Base.Source.Database, new())
            .Read(Current(fixture), fixture.Writer, sealedOutput: true))
            Assert.All(sample.Items, item => { Assert.Equal("configuration_sync_allowed", item.ReasonCode); Assert.NotNull(item.Candidate); });
        fixture.Restore();
        foreach (var resource in fixture.Session.Resources)
            Assert.Equal(1, new ConfigurationRevisionStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new()).Read(resource.Address)!.Revision);
    }

    [Fact(DisplayName = "A Closed Run Duration Cannot Be Changed Without Rejecting Startup On A Diagnostic Copy")]
    public async Task RejectsChangedDuration()
    {
        await using var fixture = new RunWriterFixture(); await fixture.StartAsync();
        var stopped = await fixture.StopAsync(); fixture.Seal(stopped); fixture.Restore();
        var frozen = Current(fixture).ClosedElapsedMilliseconds!.Value;
        fixture.Base.Execute("UPDATE configuration_run_sessions SET closed_elapsed_milliseconds=closed_elapsed_milliseconds+1;");
        var path = fixture.Base.Source.Database.DatabasePath;
        var suffixes = new[] { "", "-wal", "-shm", "-journal" };
        var before = suffixes.Where(suffix => File.Exists(path + suffix)).ToDictionary(suffix => suffix, suffix => File.ReadAllBytes(path + suffix));
        try
        {
            Assert.Equal("writer_job_closure_gate_unbound", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Base.Source.Instance,
                Path.Combine(fixture.Base.Source.Root, "duration-diagnostic"), new JournalStartupAdmission(fixture.Base.Source.Instance, new()))).Message);
            Assert.Equal(before.Keys.Order(), suffixes.Where(suffix => File.Exists(path + suffix)).Order());
            foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(path + pair.Key));
        }
        finally { fixture.Base.Execute($"UPDATE configuration_run_sessions SET closed_elapsed_milliseconds={frozen};"); }
        fixture.Base.VerifyOriginal();
    }

    private static NexusPipeline.Core.Configuration.ConfigurationRunStatus Current(RunWriterFixture fixture) =>
        new ConfigurationRunStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new())
            .Read(fixture.Session.SessionId, ConfigurationRunFixture.Subject)!;
}
