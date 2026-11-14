using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EditControllerTests
{
    [Theory(DisplayName = "The Composed Editor Retains Admission Through Real Writing Restoration And Atomic Save Or Cancel")]
    [InlineData("missing", false)]
    [InlineData("file", false)]
    [InlineData("directory", false)]
    [InlineData("missing", true)]
    [InlineData("file", true)]
    [InlineData("directory", true)]
    public async Task OwnsEditorLifecycle(string shape, bool save)
    {
        await using var fixture = new EditControllerFixture(originalShape: shape, outputShape: shape);
        var pid = await fixture.BeginAsync();
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        var changed = fixture.Base.Capture("main", "other-save", 1);
        var service = new ConfigurationRevisionService(fixture.Base.Instance, fixture.Base.Database, new(), fixture.Lease, fixture.Resources);
        Assert.Equal("configuration_host_unavailable", Assert.Throws<ConfigurationException>(() => service.Save(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", changed), changed, DateTimeOffset.UtcNow)).Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Controller.Dispose());
        var closing = Guid.NewGuid(); Assert.Equal("recovery_pending", fixture.Lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        var operation = Guid.NewGuid();
        if (save)
        {
            var saved = await fixture.Controller.SaveAsync(operation, "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow);
            Assert.True(saved.IsNew); Assert.All(saved.Items, item => Assert.Equal(Enum.Parse<FileObjectKind>(shape, true), item.Saved.Tree.Shape));
            Assert.False((await fixture.Controller.SaveAsync(operation, "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow)).IsNew);
        }
        else
        {
            Assert.Equal(ConfigurationEditPhase.Cancelled, (await fixture.Controller.CancelAsync(operation, "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow)).Phase);
            Assert.False((await fixture.Controller.CancelAsync(operation, "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow)).IsNew);
        }
        Assert.True(process.HasExited); fixture.VerifyOriginals(); Assert.Empty(fixture.Index.Current.ResourceHolds);
        Assert.NotNull(fixture.Lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
        Assert.All(fixture.Request.Targets, target => Assert.Equal(save ? 2 : 1, fixture.Base.Revisions.Read(target.Address)!.Revision));
        var reopened = StateDatabase.OpenExisting(fixture.Base.Instance, Path.Combine(fixture.Base.Root, "inspect"), new JournalStartupAdmission(fixture.Base.Instance, new()));
        ConfigurationEditInventory.VerifyReadOnly(reopened);
    }

    [Theory(DisplayName = "Declared Document Validation Runs Against The Frozen Actual Editor Output")]
    [InlineData(ConfigurationSyntax.Json, "{\"value\":true}")]
    [InlineData(ConfigurationSyntax.JsonWithComments, "{// comment\n\"value\":true}")]
    [InlineData(ConfigurationSyntax.Yaml, "value: true\n")]
    [InlineData(ConfigurationSyntax.Opaque, "binary-is-not-decoded")]
    public async Task ValidatesFrozenOutput(ConfigurationSyntax syntax, string value)
    {
        await using var fixture = new EditControllerFixture(value: value, syntax: syntax);
        await fixture.BeginAsync();
        var saved = await fixture.Controller.SaveAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow);
        Assert.True(saved.IsNew); Assert.All(saved.Items, item => Assert.Equal(2, item.Saved.Revision)); fixture.VerifyOriginals();
    }

    [Theory(DisplayName = "Invalid Declared Content Restores Originals And Keeps Old Snapshots Until An Explicit Cancel")]
    [InlineData(ConfigurationSyntax.Json, "{oops")]
    [InlineData(ConfigurationSyntax.JsonWithComments, "// comment\n{oops")]
    [InlineData(ConfigurationSyntax.Yaml, "value: [unterminated")]
    public async Task RestoresInvalidOutput(ConfigurationSyntax syntax, string value)
    {
        await using var fixture = new EditControllerFixture(value: value, syntax: syntax);
        var pid = await fixture.BeginAsync(); using var process = Process.GetProcessById(pid);
        await Assert.ThrowsAsync<ConfigurationException>(() => fixture.Controller.SaveAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow));
        Assert.True(process.HasExited); fixture.VerifyOriginals();
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Controller.Read(fixture.SessionId, "fixture")!.Phase);
        Assert.NotEmpty(fixture.Index.Current.ResourceHolds);
        Assert.All(fixture.Request.Targets, target => Assert.Equal(1, fixture.Base.Revisions.Read(target.Address)!.Revision));
        Assert.Equal(ConfigurationEditPhase.Cancelled, (await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow)).Phase);
        Assert.Empty(fixture.Index.Current.ResourceHolds);
    }

    [Fact(DisplayName = "A Changed Begin Request Cannot Cancel Or Relaunch The Already Accepted Live Editor")]
    public async Task RejectsChangedBegins()
    {
        await using var fixture = new EditControllerFixture(); var pid = await fixture.BeginAsync();
        var original = fixture.Controller.Read(fixture.SessionId, "fixture")!;
        var changed = fixture.Request with { Editor = fixture.Request.Editor! with { Arguments = fixture.Request.Editor.Arguments.Add("different") } };
        await Assert.ThrowsAsync<OperationConflictException>(() => fixture.Controller.BeginAsync(fixture.SessionId, "fixture", changed, DateTimeOffset.UtcNow));
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Controller.Read(fixture.SessionId, "fixture")!.Phase);
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        var replay = await fixture.Controller.BeginAsync(fixture.SessionId, "fixture", fixture.Request, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        Assert.Equal(original.SessionId, replay.SessionId); Assert.Equal(original.Revision, replay.Revision);
        Assert.Equal(original.Resources.ToArray(), replay.Resources.ToArray());
        Assert.False(process.HasExited);
        Assert.Equal(pid, (await OwnedCrashProcess.ReadReportAsync(fixture.ReportPath)).GetProperty("processId").GetInt32());
    }

    [Theory(DisplayName = "Invalid Completion Identity Cannot Stop The Editor Or Change Any Accepted Session")]
    [InlineData("operation")]
    [InlineData("subject")]
    [InlineData("revision")]
    public async Task RejectsForeignCompletion(string fault)
    {
        await using var fixture = new EditControllerFixture(); var pid = await fixture.BeginAsync();
        var action = () => fixture.Controller.SaveAsync(fault == "operation" ? Guid.Empty : Guid.NewGuid(), fault == "subject" ? "foreign" : "fixture",
            fixture.SessionId, fault == "revision" ? 1 : 2, DateTimeOffset.UtcNow);
        if (fault == "operation") await Assert.ThrowsAsync<ArgumentException>(action);
        else if (fault == "subject") await Assert.ThrowsAsync<OperationConflictException>(action);
        else await Assert.ThrowsAsync<ConfigurationException>(action);
        using var process = Process.GetProcessById(pid); Assert.False(process.HasExited);
        Assert.Equal(ConfigurationEditPhase.Editing, fixture.Controller.Read(fixture.SessionId, "fixture")!.Phase);
    }

    [Fact(DisplayName = "Same Physical Target Across Independent Scripts Conflicts While A Sibling Target Can Run And Finish Independently")]
    public async Task ScopesPhysicalConflicts()
    {
        await using var fixture = new EditControllerFixture(); var secondId = Guid.NewGuid();
        var overlapping = fixture.IndependentRequest(secondId, fixture.Request.Targets[0].TargetPath);
        await fixture.BeginAsync();
        Assert.Equal("configuration_host_unavailable", (await Assert.ThrowsAsync<ConfigurationException>(() => fixture.Controller.BeginAsync(
            secondId, "fixture", overlapping, DateTimeOffset.UtcNow))).Message);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Null(fixture.Controller.Read(secondId, "fixture"));
        var sibling = fixture.IndependentRequest(secondId, Path.Combine(fixture.Base.Root, "independent.json"));
        await fixture.BeginAsync(secondId, sibling);
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow);
        Assert.NotEmpty(fixture.Index.Current.ResourceHolds); Assert.All(fixture.Index.Current.ResourceHolds, hold => Assert.Equal(secondId, hold.OperationId));
        await fixture.Controller.SaveAsync(Guid.NewGuid(), "fixture", secondId, 2, DateTimeOffset.UtcNow);
        Assert.Empty(fixture.Index.Current.ResourceHolds); fixture.VerifyOriginals(); Assert.False(File.Exists(sibling.Targets[0].TargetPath));
    }

    [Fact(DisplayName = "An Ancestor Directory Claim Still Blocks Descendants After The Original Directory Has Been Replaced By The Editor")]
    public async Task PreservesAncestorClaims()
    {
        await using var fixture = new EditControllerFixture(originalShape: "directory", outputShape: "directory", directorySnapshot: true);
        var secondId = Guid.NewGuid(); var nested = Path.Combine(fixture.Request.Targets[0].TargetPath, "new-child.json");
        var request = fixture.IndependentRequest(secondId, nested);
        await fixture.BeginAsync();
        Assert.Equal("configuration_host_unavailable", (await Assert.ThrowsAsync<ConfigurationException>(() => fixture.Controller.BeginAsync(
            secondId, "fixture", request, DateTimeOffset.UtcNow))).Message);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Null(fixture.Controller.Read(secondId, "fixture"));
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow); fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "A Delete Denying Original File Handle Cancels The Accepted Preparation Without Launching Any Writer")]
    public async Task RejectsLockedOriginals()
    {
        await using var fixture = new EditControllerFixture();
        using var file = new FileStream(fixture.Request.Targets[0].TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.BeginAsync(fixture.SessionId, "fixture", fixture.Request, DateTimeOffset.UtcNow));
        Assert.Equal(ConfigurationEditPhase.Cancelled, fixture.Controller.Read(fixture.SessionId, "fixture")!.Phase);
        Assert.False(File.Exists(fixture.ReportPath)); Assert.Empty(fixture.Index.Current.ResourceHolds); fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "A Failed Final Save Keeps The Restored Session And Retained Lease And Retries Without Starting Another Writer")]
    public async Task ReplaysFailedSave()
    {
        await using var fixture = new EditControllerFixture(); var pid = await fixture.BeginAsync(); var operation = Guid.NewGuid();
        using var process = Process.GetProcessById(pid);
        var session = fixture.Controller.Read(fixture.SessionId, "fixture")!;
        var operations = new OperationQueryService(fixture.Base.Instance, fixture.Lease, fixture.Base.Database);
        fixture.Base.Execute("CREATE TRIGGER configuration.fixture_fail_controller BEFORE UPDATE ON configuration_edit_sessions WHEN NEW.phase=2 BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Controller.SaveAsync(operation, "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow));
        }
        finally { fixture.Base.Execute("DROP TRIGGER configuration.fixture_fail_controller;"); }
        Assert.True(process.HasExited);
        Assert.Null(operations.Read(operation, "fixture"));
        fixture.VerifyOriginals();
        var holds = fixture.Index.Current.ResourceHolds;
        Assert.All(holds, hold => Assert.Equal(fixture.SessionId, hold.OperationId));
        foreach (var target in fixture.Request.Targets)
            Assert.Contains(holds, hold => hold.ResourceKey == ConfigurationRevisionService.ResourceKey(target.Address));
        Assert.All(fixture.Request.Targets, target => Assert.Equal(1, fixture.Base.Revisions.Read(target.Address)!.Revision));
        var journals = new JournalStore(fixture.Base.Instance, new());
        var restored = session.Resources.Select(resource => journals.Read(resource.JournalId)).ToArray();
        Assert.All(restored, journal => Assert.Equal(JournalPhase.Committed, journal.Phase));
        var saved = await fixture.Controller.SaveAsync(operation, "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow);
        Assert.True(saved.IsNew); Assert.Equal(operation, saved.OperationId);
        var completed = fixture.Controller.Read(fixture.SessionId, "fixture")!;
        Assert.Equal(ConfigurationEditPhase.Saved, completed.Phase); Assert.Equal(operation, completed.CompletionOperationId);
        Assert.Equal("completed", operations.Read(operation, "fixture")!.Status);
        foreach (var target in fixture.Request.Targets)
        {
            var item = Assert.Single(saved.Items, item => item.Saved.Address == target.Address);
            var current = fixture.Base.Revisions.Read(target.Address)!;
            Assert.Equal(2, current.Revision); Assert.Equal(item.Saved.Tree.Entries, current.Tree.Entries);
        }
        foreach (var original in restored) Assert.Equal(original.Export(), journals.Read(original.JournalId).Export());
        Assert.Equal(pid, (await OwnedCrashProcess.ReadReportAsync(fixture.ReportPath)).GetProperty("processId").GetInt32());
        Assert.Empty(fixture.Index.Current.ResourceHolds);
        var claims = fixture.Request.Targets.SelectMany(target => ConfigurationRevisionService.Claims(target.Address)).Distinct().ToArray();
        using var acquired = fixture.Resources.TryAcquire(Guid.NewGuid(), claims).Lease;
        Assert.NotNull(acquired);
    }

    [Theory(DisplayName = "Multiple Hard Linked Original Files Or Descendants Are Rejected Before Accepting Any Edit Or Starting Its Writer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsHardLinks(bool directory)
    {
        await using var fixture = new EditControllerFixture(originalShape: directory ? "directory" : "file",
            outputShape: directory ? "directory" : "file", directorySnapshot: directory);
        var original = fixture.Request.Targets[0].TargetPath;
        if (directory) original = Path.Combine(original, "original.txt");
        var alias = Path.Combine(fixture.Base.Root, "hard-linked-original.json");
        if (!CreateHardLink(alias, original, IntPtr.Zero)) throw new IOException("fixture_hard_link_failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var before = File.ReadAllBytes(alias);
        Assert.Equal("configuration_multiple_links_rejected", (await Assert.ThrowsAsync<IOException>(() => fixture.Controller.BeginAsync(
            fixture.SessionId, "fixture", fixture.Request, DateTimeOffset.UtcNow))).Message);
        Assert.Equal(before, File.ReadAllBytes(alias)); Assert.Null(fixture.Controller.Read(fixture.SessionId, "fixture"));
        Assert.False(File.Exists(fixture.ReportPath)); fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "A New Alias To An Already Installed Candidate Cannot Become Another Scripts Editable Original")]
    public async Task RejectsInstalledAliases()
    {
        await using var fixture = new EditControllerFixture(); var secondId = Guid.NewGuid();
        var alias = Path.Combine(fixture.Base.Root, "new-candidate-alias.json"); var request = fixture.IndependentRequest(secondId, alias);
        await fixture.BeginAsync();
        if (!CreateHardLink(alias, fixture.Request.Targets[0].TargetPath, IntPtr.Zero))
            throw new IOException("fixture_hard_link_failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            Assert.Equal("configuration_multiple_links_rejected", (await Assert.ThrowsAsync<IOException>(() => fixture.Controller.BeginAsync(
                secondId, "fixture", request, DateTimeOffset.UtcNow))).Message);
            Assert.Null(fixture.Controller.Read(secondId, "fixture")); Assert.True(fixture.Index.Current.IsReady);
        }
        finally { File.Delete(alias); }
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow); fixture.VerifyOriginals();
    }

    [Fact(DisplayName = "Case Insensitive Target Requests Preserve The Exact Original Directory Entry Spelling Through Installation And Restoration")]
    public async Task PreservesEntrySpelling()
    {
        await using var fixture = new EditControllerFixture();
        var targets = fixture.Request.Targets.Select(target =>
        {
            var actual = Path.Combine(fixture.Base.Root, "MiXeD-" + target.Address.ResourceId + ".Json");
            File.Move(target.TargetPath, actual);
            var original = fixture.Originals[target.TargetPath]; fixture.Originals.Remove(target.TargetPath); fixture.Originals.Add(actual, original);
            return target with { TargetPath = Path.Combine(fixture.Base.Root, Path.GetFileName(actual).ToUpperInvariant()) };
        }).ToImmutableArray();
        var request = fixture.WithCommand(fixture.SessionId, fixture.Request with { Targets = targets }, "file", "{\"edited\":true}");
        await fixture.BeginAsync(request: request);
        var installed = Directory.EnumerateFileSystemEntries(fixture.Base.Root).Select(Path.GetFileName).ToArray();
        foreach (var original in fixture.Originals.Keys) Assert.Contains(Path.GetFileName(original), installed);
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow); fixture.VerifyOriginals();
        var restored = Directory.EnumerateFileSystemEntries(fixture.Base.Root).Select(Path.GetFileName).ToArray();
        foreach (var original in fixture.Originals.Keys) Assert.Contains(Path.GetFileName(original), restored);
    }

    [Fact(DisplayName = "Startup Rejects Weakened Physical Claim Modes On A Diagnostic Copy Without Rewriting Original Database Bytes")]
    public async Task RejectsWeakenedClaims()
    {
        await using var fixture = new EditControllerFixture(); await fixture.BeginAsync();
        await fixture.Controller.CancelAsync(Guid.NewGuid(), "fixture", fixture.SessionId, 2, DateTimeOffset.UtcNow);
        fixture.Base.Execute("UPDATE main.journal_resource_claims SET mode=0 WHERE resource_key LIKE 'filesystem:entry:%';");
        var path = fixture.Base.Database.DatabasePath;
        var evidence = new[] { "", "-wal", "-shm", "-journal" }.Where(suffix => File.Exists(path + suffix))
            .ToDictionary(suffix => suffix, suffix => File.ReadAllBytes(path + suffix));
        Assert.Equal("journal_resource_claims_unbound", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Base.Instance,
            Path.Combine(fixture.Base.Root, "unknown-claims"), new JournalStartupAdmission(fixture.Base.Instance, new()))).Message);
        Assert.Equal(evidence.Keys.Order(), new[] { "", "-wal", "-shm", "-journal" }.Where(suffix => File.Exists(path + suffix)).Order());
        foreach (var pair in evidence) Assert.Equal(pair.Value, File.ReadAllBytes(path + pair.Key));
        fixture.VerifyOriginals();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr reserved);
}
