using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RunProgressTests
{
    private const string Raw = "{\"tasks\":{\"one\":true,\"new\":false},\"count\":41}";
    private const string Normalized = "{\"tasks\":{\"one\":false,\"new\":false},\"count\":41}";
    private const string Saved = "{\"tasks\":{\"one\":false},\"count\":17}";

    [Theory(DisplayName = "Actual Fifteen Second Sampling Restores Initial Flags Only In The Candidate And Honors Auto Sync")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoresCandidateFlags(bool autoSync)
    {
        await using var fixture = new RunWriterFixture(autoSync: autoSync);
        await fixture.StartAsync();
        var capture = Capture(fixture);
        using (var immediate = capture.Read(Current(fixture), fixture.Writer, sealedOutput: false))
        {
            Assert.InRange(immediate.ElapsedMilliseconds, 0, 14999);
            Assert.All(immediate.Items, item =>
            {
                Assert.Null(item.Candidate);
                Assert.Equal(autoSync ? "configuration_sync_not_due" : "configuration_sync_disabled", item.ReasonCode);
            });
        }
        await Task.Delay(TimeSpan.FromSeconds(15));
        using (var due = capture.Read(Current(fixture), fixture.Writer, sealedOutput: false))
        {
            Assert.True(due.ElapsedMilliseconds >= 15000);
            Assert.Equal(2, due.Items.Length);
            Assert.All(due.Items, item =>
            {
                Assert.Equal(autoSync ? "configuration_sync_allowed" : "configuration_sync_disabled", item.ReasonCode);
                if (autoSync) Assert.Equal(Normalized, fixture.Base.Read(Assert.IsType<FileTreeSnapshot>(item.Candidate)));
                else Assert.Null(item.Candidate);
            });
        }
        AssertSavedOutput(fixture);
        var stopped = await fixture.StopAsync();
        Assert.All(fixture.Seal(stopped).Values, tree => Assert.Equal(Raw, fixture.Base.Read(tree)));
        fixture.Restore();
        Assert.Equal("configuration_run_sampling_closed", Assert.Throws<IOException>(() =>
            capture.Read(Current(fixture), fixture.Writer, sealedOutput: false)).Message);
    }

    [Theory(DisplayName = "Another Runtime Or Session Cannot Supply The Sampling Clock")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsSamplingClock(bool anotherRuntime)
    {
        await using var fixture = new RunWriterFixture();
        await fixture.StartAsync();
        if (anotherRuntime)
        {
            await using var other = new RunWriterFixture();
            Assert.Equal("configuration_writer_scope_unbound", Assert.Throws<IOException>(() =>
                Capture(other).Read(Current(other), fixture.Writer, sealedOutput: false)).Message);
        }
        else
        {
            var otherSession = Current(fixture) with { SessionId = Guid.NewGuid() };
            Assert.Equal("configuration_writer_scope_unbound", Assert.Throws<IOException>(() =>
                Capture(fixture).Read(otherSession, fixture.Writer, sealedOutput: false)).Message);
        }
        AssertSavedOutput(fixture);
    }

    [Fact(DisplayName = "Final Synchronization Uses The Frozen Sub Threshold Elapsed Without Borrowing Later Wall Time")]
    public async Task UsesFrozenElapsed()
    {
        await using var fixture = new RunWriterFixture();
        await fixture.StartAsync();
        var stopped = await fixture.StopAsync(); fixture.Seal(stopped);
        var frozen = fixture.Writer.ElapsedSinceStart;
        Assert.True(frozen < TimeSpan.FromSeconds(15));
        var capture = Capture(fixture); long elapsed;
        using (var sample = capture.Read(Current(fixture), fixture.Writer, sealedOutput: true))
        {
            elapsed = sample.ElapsedMilliseconds;
            Assert.All(sample.Items, item => { Assert.Equal("configuration_sync_allowed", item.ReasonCode);
                Assert.Equal(Normalized, fixture.Base.Read(Assert.IsType<FileTreeSnapshot>(item.Candidate))); });
        }
        await Task.Delay(TimeSpan.FromSeconds(15));
        Assert.Equal(frozen, fixture.Writer.ElapsedSinceStart);
        using (var sample = capture.Read(Current(fixture), fixture.Writer, sealedOutput: true))
        {
            Assert.Equal(elapsed, sample.ElapsedMilliseconds);
            Assert.All(sample.Items, item => { Assert.Equal("configuration_sync_allowed", item.ReasonCode);
                Assert.Equal(Normalized, fixture.Base.Read(Assert.IsType<FileTreeSnapshot>(item.Candidate))); });
        }
        AssertSavedOutput(fixture);
    }

    [Fact(DisplayName = "Equal Bytes Cannot Authorize Replacing The Configuration Root Outside The Task File Scope")]
    public async Task RejectsReplacedRoots()
    {
        await using var fixture = new RunWriterFixture(taskFileOnly: true);
        await fixture.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(15));
        var capture = Capture(fixture);
        using (var valid = capture.Read(Current(fixture), fixture.Writer, sealedOutput: false))
            Assert.All(valid.Items, item => Assert.Equal("configuration_sync_allowed", item.ReasonCode));
        var resource = fixture.Base.Request.Resources[0]; var target = resource.Target.TargetPath;
        var retained = target + ".retained"; var replacement = target + ".replacement";
        var bytes = File.ReadAllBytes(Path.Combine(target, "tasks.json"));
        var identity = PhysicalObjectIdentity.Read(target);
        Directory.Move(target, retained);
        try
        {
            Directory.CreateDirectory(target); File.WriteAllBytes(Path.Combine(target, "tasks.json"), bytes);
            Assert.NotEqual(identity, PhysicalObjectIdentity.Read(target));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, "tasks.json")));
            using var sample = capture.Read(Current(fixture), fixture.Writer, sealedOutput: false);
            var rejected = Assert.Single(sample.Items, item => item.Resource.Address == resource.Target.Address);
            Assert.Equal("configuration_sync_capture_rejected", rejected.ReasonCode); Assert.Null(rejected.Candidate);
            Assert.All(sample.Items.Where(item => item.Resource.Address != resource.Target.Address),
                item => Assert.Equal("configuration_sync_allowed", item.ReasonCode));
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Move(target, replacement);
            Directory.Move(retained, target);
        }
        Assert.Equal(identity, PhysicalObjectIdentity.Read(target));
        AssertSavedOutput(fixture);
        var diagnostics = new NexusPipeline.Platform.Storage.Records.RunDiagnosticStore(fixture.Base.Source.Instance,
            fixture.Base.Source.Database).Read([fixture.Session.ParentJournalId]);
        Assert.Contains(diagnostics, item => item.StepId == fixture.Session.StepId && item.ReasonCode == "configuration_sync_capture_rejected");
    }

    private static RunProgressCapture Capture(RunWriterFixture fixture) =>
        new(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
    private static ConfigurationRunStatus Current(RunWriterFixture fixture) =>
        new ConfigurationRunStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new())
            .Read(fixture.Session.SessionId, ConfigurationRunFixture.Subject)!;
    private static void AssertSavedOutput(RunWriterFixture fixture)
    {
        var revisions = new ConfigurationRevisionStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
        foreach (var resource in fixture.Base.Request.Resources)
        {
            Assert.Equal(Raw, File.ReadAllText(Path.Combine(resource.Target.TargetPath, "tasks.json")));
            var saved = revisions.Read(resource.Target.Address)!;
            Assert.Equal(1, saved.Revision); Assert.Equal(Saved, fixture.Base.Read(saved.Tree));
        }
    }
}
