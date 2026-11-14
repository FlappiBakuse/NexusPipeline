using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EditStoreTests
{
    [Theory(DisplayName = "Prepared Edit Can Cancel With Durable No Effect Evidence Without Changing Any Snapshot Or Original Object")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("directory")]
    public void CancelsUnexecutedEdits(string shape)
    {
        var fixture = new ConfigurationEditFixture(shape);
        fixture.VerifyOriginals(); fixture.MaterializeAll(); fixture.RestoreAll();
        ConfigurationEditCancel cancel = new(fixture.SessionId, 1);
        var operation = ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel);
        var status = fixture.Store.Cancel(operation, cancel, DateTimeOffset.UtcNow);
        Assert.True(status.IsNew); Assert.Equal(ConfigurationEditPhase.Cancelled, status.Phase);
        Assert.Equal(2, status.Revision);
        Assert.False(new ConfigurationEditStore(fixture.Base.Instance, fixture.Base.Database, new()).Cancel(operation, cancel, DateTimeOffset.UtcNow).IsNew);
        fixture.VerifyOriginals();
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(1, fixture.Base.Revisions.Read(change.Address)!.Revision));
        ConfigurationEditInventory.VerifyReadOnly(fixture.Base.Database);
    }

    [Theory(DisplayName = "Actual Installed Configuration Edit Restores Original Objects Before Saving Or Cancelling The Whole Session")]
    [InlineData("missing", false)]
    [InlineData("file", false)]
    [InlineData("directory", false)]
    [InlineData("missing", true)]
    [InlineData("file", true)]
    [InlineData("directory", true)]
    public void RestoresBeforeCompletion(string shape, bool save)
    {
        var fixture = new ConfigurationEditFixture(shape);
        fixture.InstallAll();
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Session.Phase); Assert.Equal(2, fixture.Session.Revision);
        foreach (var target in fixture.Request.Targets) Assert.Equal("snapshot-" + target.Address.ResourceId, File.ReadAllText(target.TargetPath));
        fixture.RestoreAll(); fixture.VerifyOriginals();
        if (save)
        {
            var completion = fixture.Completion();
            var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
            Assert.True(fixture.Store.Save(operation, completion, DateTimeOffset.UtcNow).IsNew);
            Assert.False(fixture.Store.Save(operation, completion, DateTimeOffset.UtcNow).IsNew);
        }
        else
        {
            var cancel = new ConfigurationEditCancel(fixture.SessionId, 2);
            fixture.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow);
        }
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(save ? 2 : 1, fixture.Base.Revisions.Read(change.Address)!.Revision));
        var status = fixture.Store.Read(fixture.SessionId, "fixture")!;
        Assert.Equal(save ? ConfigurationEditPhase.Saved : ConfigurationEditPhase.Cancelled, status.Phase);
        Assert.Equal(3, status.Revision); fixture.VerifyOriginals(); ConfigurationEditInventory.VerifyReadOnly(fixture.Base.Database);
    }

    [Fact(DisplayName = "Save And Cancel Require All Restoration Journals And Keep An Unsettled Session Open")]
    public void RequiresWholeRestoration()
    {
        var fixture = new ConfigurationEditFixture(); fixture.InstallAll();
        var completion = fixture.Completion();
        Assert.Throws<IOException>(() => fixture.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow));
        fixture.Exchange.Restore(fixture.Session.Resources[0].JournalId);
        var cancel = new ConfigurationEditCancel(fixture.SessionId, 2);
        Assert.Throws<IOException>(() => fixture.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(1, fixture.Base.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "Failure Of The Final Session Write Rolls Back Every Snapshot And Operation And Leaves The Restored Edit Recoverable")]
    public void RevertsFailedCompletion()
    {
        var fixture = new ConfigurationEditFixture(); fixture.InstallAll(); fixture.RestoreAll();
        var completion = fixture.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        fixture.Base.Execute("CREATE TRIGGER configuration.fixture_fail_edit BEFORE UPDATE ON configuration_edit_sessions WHEN NEW.phase=2 BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        Assert.Throws<SqliteException>(() => fixture.Store.Save(operation, completion, DateTimeOffset.UtcNow));
        Assert.Null(fixture.Store.LookupSave(operation, completion));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(1, fixture.Base.Revisions.Read(change.Address)!.Revision));
        fixture.VerifyOriginals(); fixture.Base.Execute("DROP TRIGGER configuration.fixture_fail_edit;");
        Assert.True(fixture.Store.Save(operation, completion, DateTimeOffset.UtcNow).IsNew);
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(2, fixture.Base.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "Concurrent Save And Cancel Have Exactly One Terminal Winner And Never Partially Publish Resources")]
    public async Task SerializesTerminalWrites()
    {
        var fixture = new ConfigurationEditFixture(); fixture.InstallAll(); fixture.RestoreAll();
        var completion = fixture.Completion(); var save = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        var cancel = new ConfigurationEditCancel(fixture.SessionId, 2); var cancelOperation = ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel);
        var results = await Task.WhenAll(Task.Run(() => Record.Exception(() => fixture.Store.Save(save, completion, DateTimeOffset.UtcNow))),
            Task.Run(() => Record.Exception(() => fixture.Store.Cancel(cancelOperation, cancel, DateTimeOffset.UtcNow))));
        Assert.Single(results, error => error is null); Assert.IsType<ConfigurationException>(results.Single(error => error is not null));
        var phase = fixture.Store.Read(fixture.SessionId, "fixture")!.Phase;
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(phase == ConfigurationEditPhase.Saved ? 2 : 1, fixture.Base.Revisions.Read(change.Address)!.Revision));
        fixture.VerifyOriginals(); ConfigurationEditInventory.VerifyReadOnly(fixture.Base.Database);
    }

    [Fact(DisplayName = "Session Read Begin And Completion Enforce Subject And Full Frozen Request Identity")]
    public void BindsSessionRequests()
    {
        var fixture = new ConfigurationEditFixture();
        Assert.Throws<OperationConflictException>(() => fixture.Store.Read(fixture.SessionId, "another-subject"));
        Assert.Throws<OperationConflictException>(() => fixture.Store.LookupBegin(ConfigurationEditStore.BeginIdentity(fixture.SessionId, "other", fixture.Request), fixture.Request));
        var changed = fixture.Request with { ExpectedBindingRevision = 2 };
        Assert.Throws<OperationConflictException>(() => fixture.Store.LookupBegin(ConfigurationEditStore.BeginIdentity(fixture.SessionId, "fixture", changed), changed));
        fixture.InstallAll(); fixture.RestoreAll(); var completion = fixture.Completion();
        Assert.Throws<OperationConflictException>(() => fixture.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "other", completion), completion, DateTimeOffset.UtcNow));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
    }

    [Fact(DisplayName = "Save Cannot Omit A Resource Change Its Contract Or Adopt An Unfrozen Candidate")]
    public void RequiresFrozenResources()
    {
        var fixture = new ConfigurationEditFixture(); fixture.InstallAll(); fixture.RestoreAll();
        var completion = fixture.Completion();
        var omitted = completion with { Batch = new([completion.Batch.Changes[0]]) };
        Assert.Throws<ConfigurationException>(() => fixture.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", omitted), omitted, DateTimeOffset.UtcNow));
        var altered = completion with { Batch = new(completion.Batch.Changes.Select(change => change with { Contract = change.Contract with { Revision = 2 } }).ToImmutableArray()) };
        Assert.Throws<ConfigurationException>(() => fixture.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", altered), altered, DateTimeOffset.UtcNow));
        var foreign = fixture.Base.Capture("main", "unfrozen", 1);
        var different = completion with { Batch = new(completion.Batch.Changes.Select(change => change.Address.ResourceId == "main" ? foreign : change).ToImmutableArray()) };
        Assert.Throws<IOException>(() => fixture.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", different), different, DateTimeOffset.UtcNow));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "A Real Delete Denying File Handle Prevents Installation And The Recorded Preparation Can Still Restore And Cancel")]
    public void PreservesLockedPreparation()
    {
        var fixture = new ConfigurationEditFixture();
        var target = fixture.Request.Targets.Single(item => item.Address == fixture.Session.Resources[0].Address);
        using var locked = new FileStream(target.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsAny<IOException>(() => fixture.Exchange.Install(fixture.Session.Resources[0].JournalId));
        fixture.MaterializeAll(); fixture.RestoreAll(); fixture.VerifyOriginals();
        var cancel = new ConfigurationEditCancel(fixture.SessionId, 1);
        Assert.Equal(ConfigurationEditPhase.Cancelled, fixture.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow).Phase);
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(1, fixture.Base.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "Unknown Installed Bytes Are Quarantined Without Ending The Session Or Changing User Snapshots")]
    public void IsolatesUnknownOutput()
    {
        var fixture = new ConfigurationEditFixture(); fixture.InstallAll();
        var resource = fixture.Session.Resources[0];
        var target = fixture.Request.Targets.Single(item => item.Address == resource.Address);
        File.WriteAllText(target.TargetPath, "foreign-bytes");
        Assert.Equal(ConfigurationRecoveryResult.Quarantined, fixture.Exchange.Restore(resource.JournalId));
        var cancel = new ConfigurationEditCancel(fixture.SessionId, 2);
        Assert.Throws<IOException>(() => fixture.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow));
        Assert.Equal("foreign-bytes", File.ReadAllText(target.TargetPath));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        Assert.All(fixture.Initial.Changes, change => Assert.Equal(1, fixture.Base.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "Non Bmp And Private Use Resource Ids Have The Same Frozen Order In Requests Rows And Completion")]
    public void KeepsResourceOrder()
    {
        var fixture = new ConfigurationEditFixture(resourceIds: ["\ue000", "😀"]); fixture.InstallAll(); fixture.RestoreAll();
        var completion = fixture.Completion();
        var result = fixture.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "😀", "\ue000" }, result.Items.Select(item => item.Saved.Address.ResourceId));
        ConfigurationEditInventory.VerifyReadOnly(fixture.Base.Database); fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "A Live Registered Writer Job Cannot Be Ignored Because Its Owning Host Is The Current Executor")]
    public async Task RequiresWriterClosure()
    {
        var fixture = new ConfigurationEditFixture();
        var resource = fixture.Session.Resources[0];
        using var writer = new OwnedWorkerJob();
        new JournalWriterStore(fixture.Base.Database).RegisterJob(resource.JournalId, writer);
        var report = Path.Combine(fixture.Base.Root, "worker-ready.json");
        writer.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!, [report, "config-edit-writer"], fixture.Base.Root,
            fixture.Base.Instance.InstanceId, resource.JournalId, _ => { });
        Assert.Equal("config-edit-writer", (await OwnedCrashProcess.ReadReportAsync(report)).GetProperty("nonce").GetString());
        Assert.Equal("configuration_writer_stop_unconfirmed", Assert.Throws<IOException>(() => fixture.Exchange.Install(resource.JournalId)).Message);
        fixture.VerifyOriginals();
        Assert.True(await writer.StopAsync(TimeSpan.FromSeconds(5))); Assert.Equal(0u, writer.GetAccounting().ActiveProcesses);
        fixture.MaterializeAll(); fixture.RestoreAll(); fixture.VerifyOriginals();
        var cancel = new ConfigurationEditCancel(fixture.SessionId, 1);
        Assert.Equal(ConfigurationEditPhase.Cancelled, fixture.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow).Phase);
    }

    [Fact(DisplayName = "A Missing Registered Job Remains Unknown And Cannot Authorize An Edit Or Close Its Session")]
    public void RejectsMissingJobs()
    {
        var fixture = new ConfigurationEditFixture();
        var resource = fixture.Session.Resources[0];
        using (var writer = new OwnedWorkerJob()) new JournalWriterStore(fixture.Base.Database).RegisterJob(resource.JournalId, writer);
        Assert.Equal("configuration_writer_stop_unconfirmed", Assert.Throws<IOException>(() => fixture.Exchange.Install(resource.JournalId)).Message);
        Assert.Equal(ConfigurationRecoveryResult.Quarantined, fixture.Exchange.Restore(resource.JournalId));
        Assert.Equal(ConfigurationEditPhase.Prepared, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        fixture.VerifyOriginals();
    }

    [Theory(DisplayName = "Unknown Edit Metadata Is Rejected On A Diagnostic Copy Without Changing Original Database Bytes")]
    [InlineData("instance")]
    [InlineData("contract")]
    [InlineData("revision")]
    public void PreservesUnknownMetadata(string field)
    {
        var fixture = new ConfigurationEditFixture();
        fixture.Base.Execute(field switch
        {
            "instance" => "UPDATE configuration.configuration_edit_resources SET requires_instance=1;",
            "contract" => "UPDATE configuration.configuration_edit_resources SET contract_digest='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';",
            _ => "UPDATE configuration.configuration_edit_resources SET expected_revision=12345;"
        });
        byte[] Bytes() => new[] { "", "-wal", "-shm" }.Where(suffix => File.Exists(fixture.Base.Database.DatabasePath + suffix))
            .SelectMany(suffix => File.ReadAllBytes(fixture.Base.Database.DatabasePath + suffix)).ToArray();
        var before = Bytes();
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Base.Instance, Path.Combine(fixture.Base.Root, "diagnostic-copy"),
            new JournalStartupAdmission(fixture.Base.Instance, new())));
        Assert.Equal(before, Bytes()); fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "Accepting A Grouped Edit Rolls Back Every Journal Effect Resource And Operation If Any Session Resource Fails")]
    public void RevertsFailedAcceptance()
    {
        var fixture = new ConfigurationBatchFixture();
        var store = new ConfigurationEditStore(fixture.Instance, fixture.Database, new());
        ConfigurationTarget Target(string id)
        {
            var path = Path.Combine(fixture.Root, id + ".json"); File.WriteAllText(path, "original-" + id);
            return new(new(fixture.Binding, id), 0, new(1, new('a', 64), null, false), path);
        }
        var request = new ConfigurationEditRequest(1, [Target("main"), Target("extra")]);
        var operation = ConfigurationEditStore.BeginIdentity(Guid.NewGuid(), "fixture", request);
        using var prepared = store.Prepare(operation.OperationId, request);
        var expected = prepared.Use((_, resources) => resources);
        var journals = expected.Select(resource => resource.JournalId).ToHashSet();
        var registry = new RecoveryJournalRegistry(fixture.Database);
        fixture.Execute("CREATE TRIGGER configuration.fixture_fail_begin BEFORE INSERT ON configuration_edit_resources WHEN NEW.resource_id='main' BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        Assert.Throws<SqliteException>(() => store.Begin(operation, prepared, DateTimeOffset.UtcNow));
        Assert.Null(store.Read(operation.OperationId, operation.SubjectId));
        Assert.Null(store.LookupBegin(operation, request));
        var failed = registry.ReadSnapshot();
        Assert.DoesNotContain(failed.Journals, journal => journals.Contains(journal.JournalId) || journal.OperationId == operation.OperationId);
        Assert.DoesNotContain(failed.FileEffects, effect => journals.Contains(effect.JournalId));
        Assert.DoesNotContain(failed.Isolations, isolation => journals.Contains(isolation.JournalId));
        fixture.Execute("DROP TRIGGER configuration.fixture_fail_begin;");
        var accepted = store.Begin(operation, prepared, DateTimeOffset.UtcNow);
        Assert.True(accepted.IsNew);
        Assert.Equal(operation.OperationId, accepted.SessionId);
        Assert.Equal(ConfigurationEditPhase.Prepared, accepted.Phase);
        Assert.Equal(expected.ToArray(), accepted.Resources.ToArray());
        var snapshot = registry.ReadSnapshot();
        foreach (var resource in expected)
        {
            var journal = Assert.Single(snapshot.Journals, item => item.JournalId == resource.JournalId);
            Assert.Equal(operation.OperationId, journal.OperationId);
            Assert.Equal(JournalKind.ConfigEdit, journal.Kind); Assert.Equal(JournalPhase.Prepared, journal.Phase);
            Assert.Equal(resource.PlanPath, journal.PlanPath); Assert.Equal(resource.PlanDigest, journal.PlanDigest);
            Assert.Contains(ConfigurationRevisionService.ResourceKey(resource.Address), journal.ResourceKeys);
            Assert.Equal(journal.ResourceKeys.Order(StringComparer.Ordinal), snapshot.Isolations.Where(item => item.JournalId == resource.JournalId)
                .Select(item => item.ResourceKey).Order(StringComparer.Ordinal));
            foreach (var action in new[] { FileEffectAction.PreserveOriginal, FileEffectAction.InstallCandidate,
                FileEffectAction.RetireCandidate, FileEffectAction.RestoreOriginal })
                Assert.Contains(snapshot.FileEffects, item => item.JournalId == resource.JournalId && item.Action == action &&
                    item.State == FileEffectState.Pending && item.IntentDigest == resource.PlanDigest && item.ReceiptDigest is null);
        }
        foreach (var target in request.Targets) Assert.Equal("original-" + target.Address.ResourceId, File.ReadAllText(target.TargetPath));
        ConfigurationEditInventory.VerifyReadOnly(fixture.Database);
    }

    [Theory(DisplayName = "Invalid Edit Targets Are Rejected Before Archiving Any Runtime Or Drive Content")]
    [InlineData("runtime")]
    [InlineData("ancestor")]
    [InlineData("volume")]
    public void RejectsInvalidTargets(string target)
    {
        var fixture = new ConfigurationEditFixture();
        var path = target == "runtime" ? fixture.Base.Instance.RuntimeRoot : target == "ancestor" ? fixture.Base.Root : Path.GetPathRoot(fixture.Base.Root)!;
        var request = fixture.Request with { Targets = [fixture.Request.Targets[0] with { TargetPath = path }] };
        var blobs = Path.Combine(fixture.Base.Instance.RuntimeRoot, "blobs");
        var before = Directory.GetFiles(blobs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal("configuration_edit_target_invalid", Assert.Throws<ConfigurationException>(() => fixture.Store.Prepare(Guid.NewGuid(), request)).Message);
        Assert.Equal(before, Directory.GetFiles(blobs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "Overlapping Main And Additional Targets Are Rejected Before Preparation Writes")]
    public void RejectsOverlappingTargets()
    {
        var fixture = new ConfigurationEditFixture();
        var parent = Path.Combine(fixture.Base.Root, "overlap"); Directory.CreateDirectory(parent);
        var child = Path.Combine(parent, "child.json"); File.WriteAllText(child, "unchanged");
        var request = fixture.Request with { Targets = [fixture.Request.Targets[0] with { TargetPath = parent }, fixture.Request.Targets[1] with { TargetPath = child }] };
        Assert.Equal("configuration_edit_target_overlap", Assert.Throws<ConfigurationException>(() => fixture.Store.Prepare(Guid.NewGuid(), request)).Message);
        Assert.Equal("unchanged", File.ReadAllText(child)); fixture.VerifyOriginals();
    }

    [Theory(DisplayName = "Transition To Editing Rechecks The Frozen Binding And Snapshot Baseline Without Overwriting Concurrent User Changes")]
    [InlineData("binding")]
    [InlineData("snapshot")]
    public void RechecksEditingBaseline(string change)
    {
        var fixture = new ConfigurationEditFixture();
        foreach (var resource in fixture.Session.Resources) fixture.Exchange.Install(resource.JournalId);
        if (change == "binding")
        {
            var definitions = new AutomationDefinitionStore(fixture.Base.Database); var catalog = definitions.Read();
            var binding = catalog.Bindings.Single(item => item.Id == fixture.Base.Binding);
            var edits = new AutomationChanges(catalog.Revision, [], [], [], [binding with { IsEnabled = true }], [], [], []);
            definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", edits), edits, DateTimeOffset.UtcNow);
        }
        else fixture.Base.Save(new([fixture.Base.Capture("main", "newer", 1)]));
        Assert.Equal(change == "binding" ? "configuration_binding_revision_conflict" : "configuration_revision_conflict",
            Assert.Throws<ConfigurationException>(() => fixture.Store.MarkEditing(fixture.SessionId, "fixture", 1)).Message);
        Assert.Equal(ConfigurationEditPhase.Prepared, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        fixture.RestoreAll(); var cancel = new ConfigurationEditCancel(fixture.SessionId, 1);
        fixture.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow);
        fixture.VerifyOriginals(); Assert.Equal(change == "binding" ? 1 : 2, fixture.Base.Revisions.Read(new(fixture.Base.Binding, "main"))!.Revision);
        if (change == "binding") Assert.True(new AutomationDefinitionStore(fixture.Base.Database).Read().Bindings.Single(item => item.Id == fixture.Base.Binding).IsEnabled);
    }
}
