using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EditorControlTests
{
    [Theory(DisplayName = "An Actual Declared Generic Editor Stops Restores Saves And Replays After Later Binding Changes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaysGenericEdits(bool specialized)
    {
        using var fixture = new ExecutionAdmissionFixture(specialized: specialized, saved: !specialized); var source = fixture.Source;
        await using var installed = specialized ? await InstalledEditorPackage.CreateAsync(fixture) : null;
        using var controller = new ConfigurationEditController(source.Instance, source.Database, new(), source.Lease,
            fixture.Resources, fixture.Index, activator: installed?.Activator);
        var service = new NativeEditorService(source.Instance, source.Database, new(), source.Lease, fixture.Resources, controller);
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Actual editor input", AutomationRootKind.External, Path.GetDirectoryName(executable)!);
        var script = catalog.Scripts.Single(item => item.Id == source.ScriptId);
        var declaration = new AutomationAdapterResource("native-settings", 0, "", 1, ConfigurationSyntax.Json, new([], [], []));
        if (specialized) script = script with { AdapterResources = [declaration] };
        var changes = new AutomationChanges(catalog.Revision, [root], specialized ? [script] : [], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "editor-control", changes), changes, DateTimeOffset.UtcNow);
        script = source.Definitions.Read().Scripts.Single(item => item.Id == source.ScriptId);
        string resourceId; long resourceRevision;
        if (specialized)
        {
            var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(source.Instance, new())).Capture(source.Configuration, default).Snapshot;
            var change = new ConfigurationRevisionChange(new(source.BindingA, declaration.ResourceId), 0, 1,
                AdapterConfigurationPolicy.Contract(script, declaration), tree, ConfigurationUpdateKind.Manual);
            new ConfigurationRevisionStore(source.Instance, source.Database, new()).Commit(
                ConfigurationRevisionStore.Identity(Guid.NewGuid(), "editor-control", change), change, DateTimeOffset.UtcNow);
            resourceId = declaration.ResourceId; resourceRevision = 1;
        }
        else
        {
            var resource = Assert.Single(new ValueService(source.Instance, source.Database, new(), source.Lease, fixture.Resources).Read(source.BindingA));
            resourceId = resource.ResourceId; resourceRevision = resource.Revision;
        }
        var input = Path.Combine(source.Root, "controlled-editor.json"); var report = input + ".ready";
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { operations = new[]
            { new { target = source.Configuration, kind = "file", value = "{\"fixture\":false}" } } }));
        var selection = new GenericEditorSelection(Guid.NewGuid(), source.BindingA, 1, script.Revision, root.Id, 1, Path.GetFileName(executable),
            ["configuration-editor-writer", input, report], [new(resourceId, resourceRevision)]);
        var original = File.ReadAllBytes(source.Configuration);
        if (specialized)
        {
            var denied = await Assert.ThrowsAsync<ConfigurationException>(() => service.BeginAsync("editor-control", selection, DateTimeOffset.UtcNow));
            Assert.Equal("configuration_editor_binding_open_required", denied.Message);
            Assert.Equal(original, File.ReadAllBytes(source.Configuration));
            return;
        }
        var opened = await service.BeginAsync("editor-control", selection, DateTimeOffset.UtcNow);
        var editor = await OwnedCrashProcess.ReadReportAsync(report);
        var pid = editor.GetProperty("processId").GetInt32();
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        try
        {
            Assert.Equal(ConfigurationEditPhase.Editing, opened.Phase);
            Assert.True(editor.GetProperty("consoleInput").GetBoolean());
            Assert.True(editor.GetProperty("consoleOutput").GetBoolean());
            Assert.True(editor.GetProperty("consoleError").GetBoolean());
            var saved = await controller.SaveAsync(Guid.NewGuid(), "editor-control", selection.SessionId, opened.Revision, DateTimeOffset.UtcNow);
            Assert.Equal(2, Assert.Single(saved.Items).Saved.Revision);
            Assert.True(process.HasExited); Assert.Equal(original, File.ReadAllBytes(source.Configuration));
            var current = source.Definitions.Read(); var binding = current.Bindings.Single(item => item.Id == source.BindingA);
            var changed = new AutomationChanges(current.Revision, [], [], [], [binding with { Priority = binding.Priority + 1 }], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "editor-control", changed), changed, DateTimeOffset.UtcNow);
            var replay = await service.BeginAsync("editor-control", selection, DateTimeOffset.UtcNow);
            Assert.False(replay.IsNew); Assert.Equal(ConfigurationEditPhase.Saved, replay.Phase);
            await Assert.ThrowsAsync<OperationConflictException>(() => service.BeginAsync("foreign-subject", selection, DateTimeOffset.UtcNow));
            Assert.Equal(original, File.ReadAllBytes(source.Configuration));
        }
        finally
        {
            var current = controller.Read(selection.SessionId, "editor-control");
            if (current?.Phase is ConfigurationEditPhase.Prepared or ConfigurationEditPhase.Editing)
                await controller.CancelAsync(Guid.NewGuid(), "editor-control", selection.SessionId, current.Revision, DateTimeOffset.UtcNow);
        }
    }

    [Theory(DisplayName = "Generic editor changes stay private until a new account and binding are accepted")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagesGenericDraft(bool discard)
    {
        await using var fixture = new EditControllerFixture();
        var definitions = new AutomationDefinitionStore(fixture.Base.Database);
        var catalog = definitions.Read();
        var script = catalog.Scripts.Single(item => item.Id == catalog.Bindings.Single(binding => binding.Id == fixture.Base.Binding).ScriptId);
        var user = catalog.Users[0] with { Id = Guid.NewGuid(), Revision = 0, Name = "Draft account" };
        var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, false, 0);
        var sessionId = Guid.NewGuid();
        var targets = fixture.Request.Targets.Select(target => target with {
            Address = new(binding.Id, target.Address.ResourceId), ExpectedRevision = 0
        }).ToImmutableArray();
        var request = fixture.WithCommand(sessionId, new(1, targets) { DraftBinding = new(user.Id, script.Id, script.Revision) },
            "file", "{\"draft\":true}");
        await fixture.BeginAsync(sessionId, request);
        var session = fixture.Controller.Read(sessionId, "fixture")!;
        var candidate = Guid.NewGuid();
        var staged = await fixture.Controller.SaveBindingAsync(candidate, "fixture", sessionId, session.Revision, null, DateTimeOffset.UtcNow);
        Assert.Equal(candidate, staged.DraftCandidate);
        fixture.VerifyOriginals();
        Assert.DoesNotContain(definitions.Read().Users, item => item.Id == user.Id);
        Assert.Empty(fixture.Base.Revisions.ReadCurrentRevisions(binding.Id));
        if (discard)
        {
            fixture.Controller.Drafts.Discard(candidate, "fixture");
            var error = await Assert.ThrowsAsync<ConfigurationException>(() =>
                fixture.Controller.SaveBindingAsync(candidate, "fixture", sessionId, session.Revision, null, DateTimeOffset.UtcNow));
            Assert.Equal("configuration_draft_missing", error.Message);
            fixture.VerifyOriginals();
            return;
        }
        var changes = new AutomationChanges(catalog.Revision, [], [], [user], [binding], [], [], []);
        var operation = AutomationDefinitionStore.DraftIdentity(Guid.NewGuid(), "fixture", changes, [candidate]);
        Assert.True(definitions.CommitDrafts(operation, changes, DateTimeOffset.UtcNow, [candidate], fixture.Controller.Drafts).IsNew);
        Assert.Equal(2, fixture.Base.Revisions.ReadCurrentRevisions(binding.Id).Count);
        Assert.Contains(definitions.Read().Users, item => item.Id == user.Id);
        ConfigurationEditInventory.VerifyReadOnly(fixture.Base.Database);
    }

    [Fact(DisplayName = "A Native Graphical Editor Runs Without An Extra Console And Cancelling Restores Its Files")]
    public async Task KeepsGraphicalEditor()
    {
        await using var fixture = new EditControllerFixture();
        var executable = Environment.GetEnvironmentVariable("NEXUS_TARGET_GAME_FIXTURE_EXE")!;
        var marker = Path.Combine(fixture.Base.Root, "graphical-editor.json");
        using var image = NexusPipeline.Platform.Processes.ProcessIdentityLease.HoldImage(executable);
        var request = fixture.Request with { Editor = new(executable, image.Sha256, [marker, "edit"], fixture.Base.Root) };
        var opened = await fixture.Controller.BeginAsync(fixture.SessionId, "fixture", request, DateTimeOffset.UtcNow);
        var observed = await OwnedCrashProcess.ReadReportAsync(marker);
        using var process = Process.GetProcessById(observed.GetProperty("processId").GetInt32());
        Assert.False(observed.GetProperty("consoleInput").GetBoolean());
        Assert.False(observed.GetProperty("consoleOutput").GetBoolean());
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, opened.Revision, DateTimeOffset.UtcNow);
        Assert.True(process.HasExited);
        fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "Stopping The Original Controller Actually Stops And Restores Its Live Editor Before Disposal")]
    public async Task StopsOriginalEditor()
    {
        await using var fixture = new EditControllerFixture();
        var pid = await fixture.BeginAsync();
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        await fixture.Controller.StopAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.HasExited);
        Assert.Equal(ConfigurationEditPhase.Cancelled, fixture.Controller.Read(fixture.SessionId, "fixture")!.Phase);
        fixture.VerifyOriginals();
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.BeginAsync(Guid.NewGuid(), "fixture", fixture.Request, DateTimeOffset.UtcNow));
        fixture.Controller.Dispose();
    }

    [Fact(DisplayName = "An Actual Editor Source Digest Cannot Be Changed After The Original Begin Was Accepted")]
    public async Task BindsOriginalDigest()
    {
        await using var fixture = new EditControllerFixture();
        var digest = new string('a', 64);
        var request = fixture.Request with { SourceRequestDigest = digest };
        var pid = await fixture.BeginAsync(request: request);
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        Assert.NotNull(fixture.Controller.ReadAcceptedSource(fixture.SessionId, "fixture", digest));
        using var sql = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder
            { DataSource = PhysicalPathPolicy.NativePath(fixture.Base.Database.DatabasePath), Pooling = false }.ToString());
        sql.Open(); using var command = sql.CreateCommand();
        command.CommandText = "UPDATE configuration.configuration_edit_sessions SET source_request_digest=$digest WHERE session_id=$session;";
        command.Parameters.AddWithValue("$session", fixture.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$digest", new string('b', 64)); command.ExecuteNonQuery();
        try
        {
            Assert.Throws<OperationConflictException>(() => fixture.Controller.ReadAcceptedSource(fixture.SessionId, "fixture", new string('b', 64)));
            Assert.Throws<OperationConflictException>(() => fixture.Controller.ReadAcceptedSource(fixture.SessionId, "fixture", digest));
            Assert.False(process.HasExited);
        }
        finally { command.Parameters["$digest"].Value = digest; command.ExecuteNonQuery(); }
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow);
        Assert.True(process.HasExited); fixture.VerifyOriginals();
    }
}
