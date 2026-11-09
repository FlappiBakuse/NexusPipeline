using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EditorOutputTests
{
    [Theory(DisplayName = "Actual Editor Output Is Frozen And Original Native Objects Are Restored Before Whole Session Save Or Cancel")]
    [InlineData("file", true)]
    [InlineData("directory", true)]
    [InlineData("missing", true)]
    [InlineData("file", false)]
    [InlineData("directory", false)]
    [InlineData("missing", false)]
    public async Task RestoresFrozenOutput(string shape, bool save)
    {
        using var fixture = new EditorOutputFixture();
        var edit = fixture.Edit;
        await fixture.WriteAsync(edit.Request.Targets.Select(target => (object)new { target = target.TargetPath, kind = shape, value = "edited-" + target.Address.ResourceId }).ToArray());
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); Assert.True(stopped.WasExecuted);
        fixture.Editor.Dispose();
        var completion = fixture.Seal(stopped);
        Assert.All(completion.Batch.Changes, change => Assert.Equal(Enum.Parse<FileObjectKind>(shape, true), change.Tree.Shape));
        Assert.DoesNotContain("edited-", JsonSerializer.Serialize(stopped));
        edit.RestoreAll(); edit.VerifyOriginals();
        if (save) Assert.True(edit.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow).IsNew);
        else
        {
            var cancel = new ConfigurationEditCancel(edit.SessionId, 2);
            edit.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow);
        }
        foreach (var change in completion.Batch.Changes)
        {
            var saved = edit.Base.Revisions.Read(change.Address)!; Assert.Equal(save ? 2 : 1, saved.Revision);
            if (save) Assert.Equal(change.Tree.Entries, saved.Tree.Entries);
        }
        edit.VerifyOriginals(); ConfigurationEditInventory.VerifyReadOnly(edit.Base.Database);
    }

    [Theory(DisplayName = "Atomic File Replacement And A Real Child Writer Remain Bound To The Stopped Owned Process Family")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsChildWriters(bool child)
    {
        using var fixture = new EditorOutputFixture(); var edit = fixture.Edit;
        var before = edit.Request.Targets.ToDictionary(target => target.TargetPath, target => PhysicalObjectIdentity.Read(target.TargetPath));
        await fixture.WriteAsync(edit.Request.Targets.Select(target => (object)new { target = target.TargetPath, kind = "file", atomic = true, value = "atomic-edited" }).ToArray(), child);
        var writers = await fixture.ReadWritersAsync(child);
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5));
        foreach (var target in edit.Request.Targets) Assert.NotEqual(before[target.TargetPath], PhysicalObjectIdentity.Read(target.TargetPath));
        var completion = fixture.Seal(stopped); edit.RestoreAll(); edit.VerifyOriginals();
        Assert.True(edit.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow).IsNew);
        AssertStoredClosure(edit, stopped, writers);
    }

    [Fact(DisplayName = "A Proved Unexecuted Editor Cannot Adopt Changed Bytes And Its Late Start Is Rejected")]
    public async Task RejectsChangedUnexecuted()
    {
        using var fixture = new EditorOutputFixture();
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); Assert.False(stopped.WasExecuted);
        Assert.Throws<IOException>(() => fixture.Editor.Start(fixture.Executable, fixture.ExecutableSha256, [], fixture.Edit.Base.Root));
        File.WriteAllText(fixture.Edit.Request.Targets[0].TargetPath, "unowned-change");
        Assert.Equal("configuration_edit_unexecuted_writer_changed_scene", Assert.Throws<IOException>(() => fixture.Seal(stopped)).Message);
        Assert.Equal(1, fixture.Edit.Base.Revisions.Read(fixture.Edit.Request.Targets[0].Address)!.Revision);
    }

    [Fact(DisplayName = "An Empty Stopped Editor Has A Durable Closure After Its Job Handle Is Gone And Can Still Restore And Cancel")]
    public async Task KeepsDurableClosure()
    {
        using var fixture = new EditorOutputFixture();
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); fixture.Editor.Dispose();
        Assert.False(stopped.WasExecuted); fixture.Seal(stopped); fixture.Edit.RestoreAll(); fixture.Edit.VerifyOriginals();
        var cancel = new ConfigurationEditCancel(fixture.Edit.SessionId, 2);
        Assert.Equal(ConfigurationEditPhase.Cancelled, fixture.Edit.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow).Phase);
    }

    [Fact(DisplayName = "Scoped Directory Editing Preserves Unlisted Bytes And Their Native Identities")]
    public async Task PreservesUnlistedObjects()
    {
        using var fixture = new EditorOutputFixture(new(["selected"], [FileObjectKind.Directory]), directorySnapshot: true);
        var guards = fixture.Edit.Request.Targets.ToDictionary(target => Path.Combine(target.TargetPath, "guard.json"), target =>
            PhysicalObjectIdentity.Read(Path.Combine(target.TargetPath, "guard.json")));
        await fixture.WriteAsync(fixture.Edit.Request.Targets.Select(target => (object)new { target = Path.Combine(target.TargetPath, "selected/task.json"), kind = "file", atomic = true, value = "updated-selected" }).ToArray());
        var completion = fixture.Seal(await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)));
        foreach (var guard in guards) { Assert.Equal(guard.Value, PhysicalObjectIdentity.Read(guard.Key)); Assert.Equal("guard-original", File.ReadAllText(guard.Key)); }
        fixture.Edit.RestoreAll(); fixture.Edit.VerifyOriginals();
        Assert.True(fixture.Edit.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow).IsNew);
    }

    [Theory(DisplayName = "Out Of Scope Bytes Or Identity Replacement Cannot Be Sealed As Authorized Editor Output")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnauthorizedOutput(bool sameBytes)
    {
        using var fixture = new EditorOutputFixture(new(["selected"], [FileObjectKind.Directory]), directorySnapshot: true);
        await fixture.WriteAsync(fixture.Edit.Request.Targets.Select(target => (object)new { target = Path.Combine(target.TargetPath, "guard.json"), kind = "file", atomic = true,
            value = sameBytes ? "guard-original" : "unauthorized" }).ToArray());
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(Record.Exception(() => fixture.Seal(stopped)));
        Assert.All(fixture.Edit.Initial.Changes, change => Assert.Equal(1, fixture.Edit.Base.Revisions.Read(change.Address)!.Revision));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Edit.Store.Read(fixture.Edit.SessionId, "fixture")!.Phase);
    }

    [Fact(DisplayName = "A Changed Editor Binary Is Rejected Before Execution And The Unexecuted Session Can Cancel")]
    public async Task RejectsChangedEditors()
    {
        using var fixture = new EditorOutputFixture();
        Assert.Equal("configuration_editor_image_changed", Assert.Throws<IOException>(() => fixture.Editor.Start(fixture.Executable, new('0', 64), [], fixture.Edit.Base.Root)).Message);
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); Assert.False(stopped.WasExecuted);
        fixture.Seal(stopped); fixture.Edit.RestoreAll(); fixture.Edit.VerifyOriginals();
        var cancel = new ConfigurationEditCancel(fixture.Edit.SessionId, 2);
        fixture.Edit.Store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow);
    }

    [Fact(DisplayName = "Failed Stop Receipt Publication Can Retry The Same Stopped Facts Without Relaunching Any Process")]
    public async Task ReplaysStoppedFacts()
    {
        using var fixture = new EditorOutputFixture();
        await fixture.WriteAsync(fixture.Edit.Request.Targets.Select(target => (object)new { target = target.TargetPath, kind = "file", value = "retry-stopped" }).ToArray());
        var writers = await fixture.ReadWritersAsync();
        fixture.Edit.Base.Execute("CREATE TRIGGER fixture_fail_closure BEFORE INSERT ON journal_writer_job_closures " +
            "BEGIN SELECT RAISE(ABORT,'fixture_editor_digest:' || NEW.plan_digest); END;");
        SqliteException failure;
        try
        {
            failure = await Assert.ThrowsAsync<SqliteException>(() => fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)));
            Assert.All(writers, writer => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(writer)));
        }
        finally { fixture.Edit.Base.Execute("DROP TRIGGER fixture_fail_closure;"); }
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("fixture_editor_digest:" + stopped.Reference.Sha256, failure.Message, StringComparison.Ordinal);
        fixture.Editor.Dispose(); var completion = fixture.Seal(stopped); fixture.Edit.RestoreAll(); fixture.Edit.VerifyOriginals();
        fixture.Edit.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow);
        AssertStoredClosure(fixture.Edit, stopped, writers);
    }

    [Fact(DisplayName = "A Stopped Token From Another Runtime Cannot Seal Any Other Accounts Scene")]
    public async Task RejectsForeignStops()
    {
        using var one = new EditorOutputFixture(); using var two = new EditorOutputFixture();
        var stopped = await one.Editor.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("configuration_editor_stop_proof_unbound", Assert.Throws<IOException>(() => two.Seal(stopped)).Message);
        Assert.All(two.Edit.Initial.Changes, change => Assert.Equal(1, two.Edit.Base.Revisions.Read(change.Address)!.Revision));
        one.Seal(stopped); one.Edit.RestoreAll();
        two.Seal(await two.Editor.StopAsync(TimeSpan.FromSeconds(5))); two.Edit.RestoreAll();
    }

    [Fact(DisplayName = "Late Unknown Bytes After A Seal Are Not Readopted And Cannot Change The Saved Snapshot")]
    public async Task RejectsLateChanges()
    {
        using var fixture = new EditorOutputFixture();
        await fixture.WriteAsync(fixture.Edit.Request.Targets.Select(target => (object)new { target = target.TargetPath, kind = "file", value = "frozen-edited" }).ToArray());
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); var completion = fixture.Seal(stopped);
        var target = fixture.Edit.Request.Targets[0]; File.WriteAllText(target.TargetPath, "late-unknown");
        var repeated = fixture.Seal(stopped); Assert.Equal(completion.Batch.Changes[0].Tree.Entries, repeated.Batch.Changes[0].Tree.Entries);
        var resource = fixture.Edit.Session.Resources.Single(item => item.Address == target.Address);
        Assert.Equal(ConfigurationRecoveryResult.Quarantined, fixture.Edit.Exchange.Restore(resource.JournalId));
        Assert.Equal("late-unknown", File.ReadAllText(target.TargetPath));
        Assert.Throws<IOException>(() => fixture.Edit.Store.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow));
        var content = completion.Batch.Changes.Single(item => item.Address == target.Address).Tree.Entries.Single();
        Assert.True(new BlobReferenceIndex(fixture.Edit.Base.Instance, new(), fixture.Edit.Base.Database, new(fixture.Edit.Base.Instance, new()))
            .IsReferenced(new($"blobs/content/{content.Sha256}.bin", content.SizeBytes, content.Sha256!)));
    }

    [Fact(DisplayName = "Corrupt Job Stop Evidence Cannot Authorize Restoration When The Native Job Has Disappeared")]
    public async Task RejectsCorruptClosure()
    {
        using var fixture = new EditorOutputFixture();
        await fixture.WriteAsync(fixture.Edit.Request.Targets.Select(target => (object)new { target = target.TargetPath, kind = "file", value = "edited-unrestored" }).ToArray());
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); fixture.Editor.Dispose();
        File.WriteAllText(Path.Combine(fixture.Edit.Base.Instance.RuntimeRoot, stopped.Reference.RelativePath), "{\"unknown\":true}");
        Assert.ThrowsAny<IOException>(() => fixture.Edit.Exchange.Restore(fixture.Edit.Session.Resources[0].JournalId));
        Assert.All(fixture.Edit.Request.Targets, target => Assert.Equal("edited-unrestored", File.ReadAllText(target.TargetPath)));
        Assert.All(fixture.Edit.Initial.Changes, change => Assert.Equal(1, fixture.Edit.Base.Revisions.Read(change.Address)!.Revision));
    }

    [Fact(DisplayName = "Missing Frozen Editor Write Policy Rejects Before Registering Any Native Writer Job")]
    public void RequiresWritePolicy()
    {
        var fixture = new ConfigurationEditFixture(); using var lease = InstanceLease.Acquire(fixture.Base.Instance, new());
        Assert.Equal("configuration_editor_write_policy_required", Assert.Throws<IOException>(() =>
            new ConfigurationEditorProcess(fixture.Base.Instance, fixture.Base.Database, new(), lease, fixture.SessionId, "fixture")).Message);
        var writers = new JournalWriterStore(fixture.Base.Database);
        foreach (var resource in fixture.Session.Resources)
        {
            var observed = writers.ObserveForCurrentExecutor(resource.JournalId);
            Assert.True(observed.IsComplete); Assert.Equal(WriterState.Stopped, observed.State);
        }
        Assert.Equal(ConfigurationEditPhase.Prepared, fixture.Store.Read(fixture.SessionId, "fixture")!.Phase);
        fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "Only One Editor Job Can Register And Its Closed Admission Prevents Any Later Start")]
    public async Task ClosesWriterAdmission()
    {
        var edit = new ConfigurationEditFixture(writePolicy: new([""], [FileObjectKind.File]));
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new());
        using var first = new ConfigurationEditorProcess(edit.Base.Instance, edit.Base.Database, new(), lease, edit.SessionId, "fixture");
        Assert.Equal("writer_job_registration_unbound", Assert.Throws<IOException>(() =>
            new ConfigurationEditorProcess(edit.Base.Instance, edit.Base.Database, new(), lease, edit.SessionId, "fixture")).Message);
        edit.InstallAll(); var stopped = await first.StopAsync(TimeSpan.FromSeconds(5));
        var image = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image)));
        Assert.Equal("configuration_editor_start_closed", Assert.Throws<IOException>(() => first.Start(image, hash, [], edit.Base.Root)).Message);
        var recorder = new EditOutputRecorder(edit.Base.Instance, edit.Base.Database, new(), lease);
        foreach (var resource in edit.Session.Resources) recorder.Seal(resource.JournalId, stopped);
        edit.RestoreAll(); edit.VerifyOriginals();
        AssertStoredClosure(edit, stopped, []);
    }

    private static void AssertStoredClosure(ConfigurationEditFixture edit, StoppedConfigurationEditor stopped, ProcessIdentity[] writers)
    {
        Assert.Equal(writers.Length != 0, stopped.WasExecuted);
        Assert.Equal(edit.SessionId, stopped.Closure.OperationId);
        Assert.Equal(writers.FirstOrDefault(), stopped.Closure.Root);
        if (writers.Length == 0) Assert.Empty(stopped.Closure.Family);
        foreach (var writer in writers) Assert.Contains(writer, stopped.Closure.Family);
        Assert.All(stopped.Closure.Family, writer => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(writer)));
        var closures = new JobClosureStore(edit.Base.Instance, edit.Base.Database, new());
        foreach (var resource in edit.Session.Resources)
            Assert.Equal(stopped.Reference, closures.RequireStoredReference(resource.JournalId, stopped.Closure));
    }
}
