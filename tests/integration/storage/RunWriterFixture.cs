using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class RunWriterFixture : IAsyncDisposable
{
    internal ConfigurationRunFixture Base { get; }
    internal ResourceOperationLease Root { get; }
    internal PreparedConfigurationRun Prepared { get; }
    internal ConfigurationRunStatus Session { get; }
    internal RunWriterProcess Writer { get; }
    internal EditOutputRecorder Recorder { get; }
    private StoppedConfigurationEditor? _stopped;
    private bool _restored;
    private readonly bool _installed;

    internal RunWriterFixture(bool child = false, bool install = true, bool autoSync = true, bool taskFileOnly = false)
    {
        Base = new(additional: true, nativeWriter: true, childWriter: child);
        var request = Base.Request with { AutoSyncEnabled = autoSync, SyncInitially = autoSync, SyncAtFinish = autoSync, Resources = Base.Request.Resources.Select(resource => taskFileOnly
            ? resource with { Target = resource.Target with { WritePolicy = resource.Target.WritePolicy! with { WritableRoots = ["tasks.json"] } } }
            : resource).ToImmutableArray() };
        Root = Base.Acquire(request);
        Prepared = Base.Preparation.Prepare(Root, Base.Owner, Base.Execution.Plan, Base.StepId, request);
        var parent = Base.Parent(Prepared); Base.Accept(parent);
        var store = new ConfigurationRunStore(Base.Source.Instance, Base.Source.Database, new());
        var id = Guid.NewGuid();
        Session = store.Begin(ConfigurationRunStore.BeginIdentity(id, ConfigurationRunFixture.Subject, Guid.Parse(parent.JournalId), Base.StepId, Prepared),
            Guid.Parse(parent.JournalId), Base.StepId, Prepared, DateTimeOffset.UtcNow);
        Writer = new(Base.Source.Instance, Base.Source.Database, new(), Base.Lease, id, ConfigurationRunFixture.Subject);
        Recorder = new(Base.Source.Instance, Base.Source.Database, new(), Base.Lease);
        if (install)
        {
            foreach (var resource in Session.Resources) Base.Install(resource.JournalId);
            Session = store.MarkRunning(id, ConfigurationRunFixture.Subject, 1);
        }
        _installed = install;
    }

    internal async Task<int[]> StartAsync(bool child = false)
    {
        var root = Writer.Start();
        var ready = await OwnedCrashProcess.ReadReportAsync(Base.NativeReport);
        Assert.Equal(root.ProcessId, ready.GetProperty("processId").GetInt32());
        if (!child) return [root.ProcessId];
        var childReady = await OwnedCrashProcess.ReadReportAsync(Base.NativeReport + ".child");
        var childId = childReady.GetProperty("processId").GetInt32();
        Assert.NotEqual(root.ProcessId, childId); return [root.ProcessId, childId];
    }

    internal async Task<StoppedConfigurationEditor> StopAsync() => _stopped ??= await Writer.StopAsync(TimeSpan.FromSeconds(10));
    internal IReadOnlyDictionary<Guid, FileTreeSnapshot> Seal(StoppedConfigurationEditor stopped) =>
        Session.Resources.ToDictionary(item => item.JournalId, item => Recorder.Seal(item.JournalId, stopped));
    internal void Restore(bool verifySavedBaseline = true)
    {
        Base.Restore(); _restored = true;
        if (!verifySavedBaseline) return;
        foreach (var resource in Session.Resources)
        {
            var saved = new ConfigurationRevisionStore(Base.Source.Instance, Base.Source.Database, new()).Read(resource.Address)!;
            Assert.Equal(1, saved.Revision); Assert.Contains("\"one\":false", Base.Read(saved.Tree));
        }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_restored) { var stopped = await StopAsync(); if (_installed) Seal(stopped); Restore(); }
        }
        finally { Writer.Dispose(); Prepared.Dispose(); Root.Dispose(); Base.Dispose(); }
    }
}
