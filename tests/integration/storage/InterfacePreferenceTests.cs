using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InterfacePreferenceTests
{
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

        var nextValues = new InterfaceValues("system", ["running", "history-duration"], ["service", "advanced"], ["proxy"], "zh-CN");
        var nextOperation = Guid.NewGuid();
        var next = preferences.Save(nextOperation, "local:interface-test", 1, nextValues);
        Assert.Equal(2, next.Revision);
        AssertSnapshot(next, preferences.Save(nextOperation, "local:interface-test", 1, nextValues));

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
        Assert.Equal(expected.Values.PinnedSettings, actual.Values.PinnedSettings);
        Assert.Equal(expected.Values.CollapsedSettings, actual.Values.CollapsedSettings);
    }
}
