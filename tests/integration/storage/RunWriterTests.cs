using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RunWriterTests
{
    [Fact(DisplayName = "A New Hard Link To Live Run Output Cannot Be Sealed Or Change Either Saved Baseline")]
    public async Task RejectsNewAliases()
    {
        await using var fixture = new RunWriterFixture();
        await fixture.StartAsync(); var stopped = await fixture.StopAsync();
        var target = fixture.Prepared.Use(resources => resources[0].Plan.TargetPath);
        var alias = Path.Combine(fixture.Base.Source.Root, "outside-run-alias.json");
        Assert.True(CreateHardLink(alias, Path.Combine(target, "tasks.json"), IntPtr.Zero));
        try
        {
            var journal = fixture.Session.Resources[0].JournalId;
            var before = new JournalStore(fixture.Base.Source.Instance, new()).Read(journal).Export();
            Assert.Equal("configuration_multiple_links_rejected", Assert.Throws<IOException>(() => fixture.Recorder.Seal(journal, stopped)).Message);
            Assert.Equal(before, new JournalStore(fixture.Base.Source.Instance, new()).Read(journal).Export());
            foreach (var resource in fixture.Session.Resources)
                Assert.Equal(1, new ConfigurationRevisionStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new()).Read(resource.Address)!.Revision);
        }
        finally { File.Delete(alias); }
        fixture.Seal(stopped); fixture.Restore();
    }

    [Fact(DisplayName = "An Empty Owned Run Job Closes Durably Rejects Late Start And Restores Both Original Scenes")]
    public async Task ClosesEmptyJobs()
    {
        await using var fixture = new RunWriterFixture();
        var stopped = await fixture.StopAsync(); Assert.False(stopped.WasExecuted);
        fixture.Writer.Dispose();
        Assert.Throws<IOException>(() => fixture.Writer.Start());
        var output = fixture.Seal(stopped);
        Assert.All(output.Values, tree => Assert.Contains("\"one\":true", fixture.Base.Read(tree)));
        fixture.Restore();
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
    }

    [Theory(DisplayName = "Actual Run Root And Child Writing Seal Raw Selected Flags And Progress Before Restoring Original Objects")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoresCollectedProgress(bool child)
    {
        await using var fixture = new RunWriterFixture(child);
        var ids = await fixture.StartAsync(child);
        var stopped = await fixture.StopAsync(); Assert.True(stopped.WasExecuted);
        fixture.Writer.Dispose();
        foreach (var id in ids) Assert.False(IsRunning(id));
        var output = fixture.Seal(stopped);
        Assert.Equal(2, output.Count);
        foreach (var tree in output.Values)
            Assert.Equal("{\"tasks\":{\"one\":true,\"new\":false},\"count\":41}", fixture.Base.Read(tree));
        foreach (var resource in fixture.Session.Resources)
            Assert.Equal(output[resource.JournalId].Entries, fixture.Recorder.Seal(resource.JournalId, stopped).Entries);
        fixture.Restore();
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
    }

    [Fact(DisplayName = "A Stopped Run Token Cannot Seal Another Runtime Groups Installed Configurations")]
    public async Task RejectsForeignStops()
    {
        await using var first = new RunWriterFixture();
        await using var second = new RunWriterFixture();
        var stopped = await first.StopAsync();
        var journal = second.Session.Resources[0].JournalId;
        var before = new JournalStore(second.Base.Source.Instance, new()).Read(journal).Export();
        Assert.Equal("configuration_editor_stop_proof_unbound", Assert.Throws<IOException>(() => second.Recorder.Seal(journal, stopped)).Message);
        Assert.Equal(before, new JournalStore(second.Base.Source.Instance, new()).Read(journal).Export());
    }

    [Fact(DisplayName = "Closing Run Writer Admission Rejects Another Job Before It Can Register Or Execute")]
    public async Task ClosesWriterAdmission()
    {
        await using var fixture = new RunWriterFixture(install: false);
        await fixture.StopAsync(); var count = fixture.Base.Count("journal_writer_jobs");
        var current = new ConfigurationRunStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new())
            .Read(fixture.Session.SessionId, ConfigurationRunFixture.Subject)!;
        Assert.Equal(ConfigurationRunPhase.Prepared, current.Phase); Assert.True(current.IsWriterAdmissionClosed);
        Assert.Equal("configuration_run_revision_conflict", Assert.Throws<ConfigurationException>(() =>
            new RunWriterProcess(fixture.Base.Source.Instance, fixture.Base.Source.Database, new(),
                fixture.Base.Lease, fixture.Session.SessionId, ConfigurationRunFixture.Subject)).Message);
        Assert.Equal(count, fixture.Base.Count("journal_writer_jobs"));
        Assert.Throws<IOException>(() => fixture.Writer.Start());
    }

    [Fact(DisplayName = "Failed Run Closure Publication Retries The Same Stopped Native Facts Without Starting Any New Process")]
    public async Task ReplaysStoppedFacts()
    {
        await using var fixture = new RunWriterFixture(child: true);
        var ids = await fixture.StartAsync(child: true);
        fixture.Base.Execute("CREATE TRIGGER fixture_fail_run_stop BEFORE INSERT ON journal_writer_job_closures BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try { await Assert.ThrowsAsync<SqliteException>(() => fixture.Writer.StopAsync(TimeSpan.FromSeconds(10))); }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_fail_run_stop;"); }
        foreach (var id in ids) Assert.False(IsRunning(id));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_closures"));
        var stoppedFacts = ClosureFiles(fixture);
        Assert.Single(stoppedFacts);
        Assert.Throws<IOException>(() => fixture.Writer.Start());
        var stopped = await fixture.StopAsync(); Assert.True(stopped.WasExecuted);
        Assert.Equal(stoppedFacts, ClosureFiles(fixture));
        fixture.Seal(stopped); fixture.Restore();
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
    }

    [Fact(DisplayName = "A Run Sealed Raw Progress Blob Remains Referenced Under The Actual Deletion Guard After Restoration")]
    public async Task ProtectsSealedProgress()
    {
        await using var fixture = new RunWriterFixture();
        await fixture.StartAsync(); var stopped = await fixture.StopAsync(); var output = fixture.Seal(stopped);
        fixture.Restore(); fixture.Prepared.Dispose();
        var entry = output.Values.First().Entries.Single(item => item.Kind == FileObjectKind.File);
        var blob = new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!);
        var instance = fixture.Base.Source.Instance; var blobs = fixture.Base.Source.Blobs;
        var plan = new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [blob]);
        var cleanup = new BlobCleanup(instance, blobs, new(instance, new(), fixture.Base.Source.Database, blobs));
        Assert.Equal(CleanupItemState.Referenced, cleanup.Inspect(Assert.Single(plan.Items)));
        Assert.Equal(CleanupStepResult.SkippedReferenced, cleanup.RemoveVerified(Assert.Single(plan.Items)));
        blobs.Verify(blob);
    }

    private static bool IsRunning(int id)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr reserved);

    private static string[] ClosureFiles(RunWriterFixture fixture) =>
        Directory.GetFiles(Path.Combine(fixture.Base.Source.Instance.RuntimeRoot, "blobs", "plans"), "*.json")
            .Where(path =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                return document.RootElement.TryGetProperty("format", out var format) && format.GetString() == "nexus.writer.job-closure";
            }).Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" +
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
