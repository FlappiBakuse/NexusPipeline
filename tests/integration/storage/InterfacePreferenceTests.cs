using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InterfacePreferenceTests
{
    [Fact(DisplayName = "Preference Publication Preserves A Foreign Stage And Releases Its Own Failed Stage")]
    public void KeepsForeignStageOnCreateCollision()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var path = Path.Combine(fixture.Source.Instance.RuntimeRoot, "state", "interface.json");
        var staging = path + ".pending-test";
        File.WriteAllText(staging, "foreign data");
        Assert.Throws<IOException>(() => InterfacePreferences.Publish(staging, path, _ => Assert.Fail("foreign file opened")));
        Assert.Equal("foreign data", File.ReadAllText(staging));
        File.Delete(staging);

        var failure = new IOException("fixture write failed");
        Assert.Same(failure, Assert.Throws<IOException>(() => InterfacePreferences.Publish(staging, path, _ => throw failure)));
        Assert.False(File.Exists(staging));
        Assert.False(File.Exists(path));
    }

    [Theory(DisplayName = "A Preference Write Failure Preserves Foreign Objects And Reports A Separate Stage Cleanup Error")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesReplacedStageOnFailure(bool directory)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var path = Path.Combine(fixture.Source.Instance.RuntimeRoot, "state", "interface.json");
        var staging = path + ".pending-test";
        var displaced = path + ".displaced-test";
        var foreign = directory ? Path.Combine(staging, "foreign") : staging;
        File.WriteAllText(path, "existing preferences");
        var primary = new IOException("fixture write failed after stage replacement");
        Assert.Same(primary, Assert.Throws<IOException>(() => InterfacePreferences.Publish(staging, path, output =>
        {
            output.WriteByte(17);
            output.Dispose();
            File.Move(staging, displaced);
            if (directory) Directory.CreateDirectory(staging);
            File.WriteAllText(foreign, "foreign replacement");
            throw primary;
        })));
        Assert.Equal("interface_stage_cleanup_unknown", Assert.Single(CleanupFailures.Read(primary)).Message);
        Assert.Equal("existing preferences", File.ReadAllText(path));
        Assert.Equal("foreign replacement", File.ReadAllText(foreign));
        Assert.Equal([17], File.ReadAllBytes(displaced));
    }

    [Fact(DisplayName = "The Reopened Preference Stage Must Preserve Its Verified Bytes Even When The Object Identity Matches")]
    public void RejectsChangedStageBytes()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var parent = Path.Combine(fixture.Source.Instance.RuntimeRoot, "state");
        var staging = Path.Combine(parent, "interface.pending-test");
        byte[] original = [0, 17]; byte[] changed = [1, 17];
        File.WriteAllBytes(staging, original);
        var saved = new FileObjectDeletion(staging, PhysicalObjectIdentity.Read(parent), PhysicalObjectIdentity.Read(staging),
            FileObjectKind.File, original.Length, Convert.ToHexStringLower(SHA256.HashData(original)));
        using (var ready = InterfacePreferences.OpenReadyStage(saved)) Assert.Equal(original[0], ready.ReadByte());
        File.WriteAllBytes(staging, changed);
        Assert.Equal(saved.Identity, PhysicalObjectIdentity.Read(staging));
        Assert.Equal("interface_stage_publication_unknown", Assert.Throws<IOException>(() =>
            InterfacePreferences.OpenReadyStage(saved).Dispose()).Message);
        Assert.Equal(changed, File.ReadAllBytes(staging));
    }

    [Fact(DisplayName = "Interface Preferences Replay Their CAS Snapshot, Reject Stale Writes, Survive Reopen And Preserve Invalid Saved Bytes")]
    public void PreservesPreferences()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var preferences = new InterfacePreferences(source.Lease, fixture.Resources);
        Assert.Equal(0, preferences.Read().Revision);

        var firstValues = new InterfaceValues("light", ["history-counts"], ["proxy"], ["service"], "en-US");
        var firstOperation = Guid.NewGuid();
        var first = preferences.Save(firstOperation, "local:interface-test", 0, firstValues);
        Assert.Equal(1, first.Revision);
        Assert.Equal(firstOperation, first.OperationId);
        AssertSnapshot(first, preferences.Save(firstOperation, "local:interface-test", 0, firstValues));
        Assert.Throws<InterfaceConflictException>(() => preferences.Save(Guid.NewGuid(), "local:interface-test", 0, firstValues));
        Assert.Throws<InterfaceConflictException>(() => preferences.Save(firstOperation, "local:interface-test", 0,
            firstValues with { Theme = "dark" }));
        Assert.Equal(1, preferences.Read().Revision);

        var nextValues = new InterfaceValues("system", ["running", "history-duration"], ["service", "advanced"], ["proxy"], "zh-CN",
            ["game-activities", "status"]);
        var nextOperation = Guid.NewGuid();
        var next = preferences.Save(nextOperation, "local:interface-test", 1, nextValues);
        Assert.Equal(2, next.Revision);
        Assert.Equal(new[] { "game-activities", "status" }, next.Values.DashboardAvailableCards);
        AssertSnapshot(next, preferences.Save(nextOperation, "local:interface-test", 1, nextValues));
        Assert.Throws<ArgumentException>(() => preferences.Save(Guid.NewGuid(), "local:interface-test", 2,
            nextValues with { DashboardAvailableCards = ["status", "status"] }));
        Assert.Throws<ArgumentException>(() => preferences.Save(Guid.NewGuid(), "local:interface-test", 2,
            nextValues with { DashboardAvailableCards = ["bad/key"] }));

        var path = Path.Combine(source.Instance.RuntimeRoot, "state", "interface.json");
        var savedBytes = File.ReadAllBytes(path);
        source.Lease.Dispose();
        using var reopenedLease = InstanceLease.Acquire(source.Instance, new());
        var unexpected = Path.Combine(source.Instance.RuntimeRoot, "state", "unexpected.db");
        File.WriteAllBytes(unexpected, [9, 7, 5]);
        try
        {
            Assert.Equal("database_group_unknown_entry", Assert.Throws<IOException>(() =>
                StateDatabase.OpenCurrent(source.Instance, reopenedLease)).Message);
            Assert.Equal("database_group_unknown_entry", Assert.Throws<IOException>(() =>
                StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "interface-unknown"),
                    new JournalStartupAdmission(source.Instance, new()), reopenedLease)).Message);
            Assert.Equal(new byte[] { 9, 7, 5 }, File.ReadAllBytes(unexpected));
        }
        finally { File.Delete(unexpected); }

        Assert.Equal(source.Instance.InstanceId, StateDatabase.OpenCurrent(source.Instance, reopenedLease).InstanceId);
        Assert.Equal(source.Instance.InstanceId, StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "interface-reopen"),
            new JournalStartupAdmission(source.Instance, new()), reopenedLease).InstanceId);
        var reopened = new InterfacePreferences(reopenedLease, fixture.Resources);
        AssertSnapshot(next, reopened.Read());
        Assert.Equal(savedBytes, File.ReadAllBytes(path));

        var invalid = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "foreign.interface.preferences", version = 1, instanceId = source.Instance.InstanceId,
            revision = 2, values = nextValues, operationId = Guid.NewGuid(), subject = "local:interface-test",
            requestSha256 = new string('0', 64)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllBytes(path, invalid);
        var before = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => reopened.Save(Guid.NewGuid(), "local:interface-test", 2, firstValues));
        Assert.Throws<IOException>(() => reopened.Read());
        Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(File.ReadAllBytes(path)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "interface.json.*.tmp"));
    }

    [Fact(DisplayName = "Event Revision Uses The Live Instance Publication Without Waiting Behind Execution Work")]
    public async Task PublishesLightweightRevision()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var preferences = new InterfacePreferences(source.Lease, fixture.Resources);
        var values = preferences.Read().Values with { Theme = "light" };
        preferences.Save(Guid.NewGuid(), "local:interface-test", 0, values);
        Assert.Equal(1, preferences.PublishedRevision);
        var version = preferences.ChangeVersion;
        var reopened = new InterfacePreferences(source.Lease, fixture.Resources);
        Assert.Equal(1, reopened.PublishedRevision);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var publication = Task.Run(() => source.Lease.PublishExecution(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, await Task.Run(() => preferences.PublishedRevision).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); await publication; }
        preferences.Save(Guid.NewGuid(), "local:interface-test", 1, values with { Theme = "dark" });
        Assert.Equal(2, preferences.PublishedRevision);
        Assert.True(preferences.ChangeVersion > version);
        Assert.Throws<InterfaceConflictException>(() => preferences.Save(Guid.NewGuid(), "local:interface-test", 1, values));
        Assert.Equal(2, preferences.PublishedRevision);
        source.Lease.Dispose();
        Assert.Throws<IOException>(() => preferences.PublishedRevision);
    }

    private static void AssertSnapshot(InterfaceSnapshot expected, InterfaceSnapshot actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.Values.Locale, actual.Values.Locale);
        Assert.Equal(expected.Values.Theme, actual.Values.Theme);
        Assert.Equal(expected.Values.DashboardCards, actual.Values.DashboardCards);
        Assert.Equal(expected.Values.DashboardAvailableCards, actual.Values.DashboardAvailableCards);
        Assert.Equal(expected.Values.PinnedSettings, actual.Values.PinnedSettings);
        Assert.Equal(expected.Values.CollapsedSettings, actual.Values.CollapsedSettings);
    }
}
