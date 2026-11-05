using System.Diagnostics;
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
        var opened = await service.BeginAsync("editor-control", selection, DateTimeOffset.UtcNow);
        var pid = (await OwnedCrashProcess.ReadReportAsync(report)).GetProperty("processId").GetInt32();
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        try
        {
            Assert.Equal(ConfigurationEditPhase.Editing, opened.Phase);
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
        using var sql = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = PhysicalPathPolicy.NativePath(fixture.Base.Database.DatabasePath), Pooling = false }.ToString());
        sql.Open(); using var command = sql.CreateCommand();
        command.CommandText = "UPDATE configuration_edit_sessions SET source_request_digest=$digest WHERE session_id=$session;";
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
