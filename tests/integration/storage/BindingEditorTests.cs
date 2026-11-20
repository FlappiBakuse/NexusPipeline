using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BindingEditorTests
{
    [Theory(DisplayName = "The binding editor isolates siblings, rejects a second profile and atomically binds a replacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavesOneConfirmedProfile(bool exitsBeforeSave)
    {
        await using var fixture = new InstalledBettergiFixture();
        await fixture.InstallAsync();
        var source = fixture.Source;
        var directory = Path.Combine(source.Root, "profile-editor");
        var profiles = Directory.CreateDirectory(Path.Combine(directory, "User", "OneDragon")).FullName;
        InstalledEditorPackage.PrepareImage(directory, "BetterGI.exe");
        var first = Path.Combine(profiles, "first.json");
        var second = Path.Combine(profiles, "second.json");
        var replacement = Path.Combine(profiles, "replacement.json");
        var empty = Path.Combine(profiles, "empty.json");
        File.WriteAllText(first, "{\"Name\":\"A\"}");
        File.WriteAllText(second, "{\"Name\":\"B\"}");
        File.WriteAllText(Path.Combine(directory, "User", "config.json"), "{}");
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Profile editor", AutomationRootKind.Script, directory);
        var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId) with {
            RootId = root.Id, Executable = new(root.Id, "BetterGI.exe"), Arguments = [], ConfigurationPaths = [], AdapterResources = [], LogPaths = []
        };
        var binding = new AutomationBinding(Guid.NewGuid(), 0, source.UserA, script.Id, false, 0);
        var change = new AutomationChanges(catalog.Revision, [root], [script], [], [binding], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "editor", change), change, DateTimeOffset.UtcNow);
        script = source.Definitions.Read().Scripts.Single(item => item.Id == script.Id);
        var contracts = new ContractReader();
        var resources = fixture.Execution.Resources;
        var observer = new LateProfile(replacement);
        using var controller = new ConfigurationEditController(source.Instance, source.Database, contracts,
            source.Lease, resources, fixture.Execution.Index, observer, fixture.Installation.Activator);
        var native = new NativeEditorService(source.Instance, source.Database, contracts, source.Lease, resources, controller);
        var sources = new ScriptSourceService(source.Instance, source.Database, source.Lease, resources, contracts,
            fixture.Installation.Roots, fixture.Installation.Activator);
        var editor = new BindingEditorService(source.Instance, source.Database, contracts, resources, controller, native, sources);
        var request = new BindingEditorSelection(Guid.NewGuid(), binding.Id, 1, script.Revision, null);
        try
        {
            if (exitsBeforeSave) File.WriteAllText(empty, "");
            var offered = await editor.OpenAsync("editor", request, () => { }, default);
            Assert.Null(offered.Session);
            Assert.Equal(2, offered.Options.Count);
            if (exitsBeforeSave) File.Delete(empty);
            var selected = request with { CandidateId = offered.Options[0].Id };
            var session = Assert.IsType<ConfigurationEditStatus>((await editor.OpenAsync("editor", selected, () => { }, default)).Session);
            Assert.True(File.Exists(first));
            Assert.False(File.Exists(second));
            var attached = await editor.OpenAsync("editor", request with { SessionId = Guid.NewGuid() }, () => { }, default);
            Assert.Equal(session.SessionId, attached.Session?.SessionId);
            File.WriteAllText(replacement, "{\"Name\":\"C\"}");
            var rejected = await controller.SaveBindingAsync(Guid.NewGuid(), "editor", session.SessionId, session.Revision, null, DateTimeOffset.UtcNow);
            Assert.Equal("configuration_editor_multiple_profiles", rejected.Inspection?.Issue);
            Assert.True(File.Exists(replacement));
            Assert.Null(source.Definitions.Read().Bindings.Single(item => item.Id == binding.Id).Source);
            File.Delete(first);
            var review = await controller.SaveBindingAsync(Guid.NewGuid(), "editor", session.SessionId, session.Revision, null, DateTimeOffset.UtcNow);
            Assert.Equal("configuration_editor_confirm_replacement", review.Inspection?.Issue);
            var candidate = Assert.Single(review.Inspection!.Options).Id;
            var operation = Guid.NewGuid();
            var accepted = await controller.SaveBindingAsync(operation, "editor", session.SessionId, session.Revision, candidate, DateTimeOffset.UtcNow);
            Assert.True(accepted.Saved?.IsNew);
            Assert.Equal(new[] { "startOneDragon", "C" }, source.Definitions.Read().Bindings.Single(item => item.Id == binding.Id).Source!.Arguments);
            Assert.Equal("{\"Name\":\"A\"}", File.ReadAllText(first));
            Assert.Equal("{\"Name\":\"B\"}", File.ReadAllText(second));
            Assert.False(File.Exists(replacement));
            var replay = await controller.SaveBindingAsync(operation, "editor", session.SessionId, session.Revision, candidate, DateTimeOffset.UtcNow);
            Assert.False(replay.Saved?.IsNew);
            ConfigurationEditInventory.VerifyReadOnly(source.Database);
            ConfigurationBatchInventory.VerifyReadOnly(source.Instance, source.Database, contracts);
            File.Delete(Path.Combine(directory, "nexus-editor-started"));
            var reopened = Assert.IsType<ConfigurationEditStatus>((await editor.OpenAsync("editor",
                request with { SessionId = Guid.NewGuid() }, () => { }, default)).Session);
            Assert.Equal("{\"Name\":\"C\"}", File.ReadAllText(replacement));
            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
            if (exitsBeforeSave)
            {
                File.WriteAllText(first, "{\"Name\":\"X\"}");
                await ExitEditorAsync(directory);
            }
            else observer.Arm(first);
            var late = await controller.SaveBindingAsync(Guid.NewGuid(), "editor", reopened.SessionId, reopened.Revision, null, DateTimeOffset.UtcNow);
            Assert.Equal("configuration_editor_multiple_profiles", late.Inspection?.Issue);
            Assert.True(late.CanResume);
            using (File.Open(Path.Combine(directory, "BetterGI.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAnyAsync<IOException>(() => controller.ResumeBindingAsync(
                    "editor", reopened.SessionId, reopened.Revision, Guid.NewGuid()));
            if (exitsBeforeSave)
                await controller.CancelAsync(Guid.NewGuid(), "editor", reopened.SessionId, reopened.Revision, DateTimeOffset.UtcNow);
            else
            {
                var resumed = await controller.ResumeBindingAsync("editor", reopened.SessionId, reopened.Revision, Guid.NewGuid());
                Assert.Equal("{\"Name\":\"X\"}", File.ReadAllText(first));
                await controller.CancelAsync(Guid.NewGuid(), "editor", resumed.SessionId, resumed.Revision, DateTimeOffset.UtcNow);
            }
            Assert.Equal("{\"Name\":\"A\"}", File.ReadAllText(first));
            Assert.Equal("{\"Name\":\"B\"}", File.ReadAllText(second));
        }
        finally { await controller.StopAsync(TimeSpan.FromSeconds(25)); }
    }

    [Fact(DisplayName = "A bound profile blocks an ambiguous native launch and supplies its actual configuration to observation")]
    public async Task RunsBoundProfile()
    {
        await using var fixture = new InstalledBettergiFixture();
        await fixture.InstallAsync();
        var source = fixture.Source;
        var directory = Path.Combine(source.Root, "bound-profile-run");
        var profiles = Directory.CreateDirectory(Path.Combine(directory, "User", "OneDragon")).FullName;
        Directory.CreateDirectory(Path.Combine(directory, "log"));
        InstalledEditorPackage.PrepareImage(directory, "BetterGI.exe");
        const string profile = "{\"Name\":\"Daily\",\"TaskEnabledList\":{\"mail\":true},\"TaskDefinitions\":{\"mail\":\"领取邮件\"}}";
        var selected = Path.Combine(profiles, "z.json");
        var duplicate = Path.Combine(profiles, "a.json");
        var marker = Path.Combine(directory, "nexus-runtime-started");
        File.WriteAllText(selected, profile);
        File.WriteAllText(Path.Combine(directory, "User", "config.json"), "{\"selectedOneDragonFlowConfigName\":\"Daily\"}");
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Bound profile", AutomationRootKind.Script, directory);
        var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId) with {
            Id = Guid.NewGuid(), Revision = 0, RootId = root.Id, Executable = new(root.Id, "BetterGI.exe"),
            Arguments = [], ConfigurationPaths = [], AdapterResources = [], LogPaths = []
        };
        var binding = new AutomationBinding(Guid.NewGuid(), 0, source.UserA, script.Id, true, 0);
        var change = new AutomationChanges(catalog.Revision, [root], [script], [], [binding], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "editor", change), change, DateTimeOffset.UtcNow);
        script = source.Definitions.Read().Scripts.Single(item => item.Id == script.Id);
        var contracts = new ContractReader();
        var resources = fixture.Execution.Resources;
        using var controller = new ConfigurationEditController(source.Instance, source.Database, contracts,
            source.Lease, resources, fixture.Execution.Index, activator: fixture.Installation.Activator);
        var native = new NativeEditorService(source.Instance, source.Database, contracts, source.Lease, resources, controller);
        var sources = new ScriptSourceService(source.Instance, source.Database, source.Lease, resources, contracts,
            fixture.Installation.Roots, fixture.Installation.Activator);
        var editor = new BindingEditorService(source.Instance, source.Database, contracts, resources, controller, native, sources);
        var request = new BindingEditorSelection(Guid.NewGuid(), binding.Id, 1, script.Revision, null);
        var siblingDirectory = Directory.CreateDirectory(Path.Combine(source.Root, "another-bettergi")).FullName;
        InstalledEditorPackage.PrepareImage(siblingDirectory, "BetterGI.exe");
        try
        {
            using (var sibling = Process.Start(new ProcessStartInfo(Path.Combine(siblingDirectory, "BetterGI.exe"))
                { WorkingDirectory = siblingDirectory, UseShellExecute = false, CreateNoWindow = true })!)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (!File.Exists(Path.Combine(siblingDirectory, "nexus-editor-started")))
                        await Task.Delay(20, deadline.Token);
                    var occupied = await Assert.ThrowsAsync<IOException>(() => editor.OpenAsync("editor", request, () => { }, default));
                    Assert.Equal("upstream_program_already_running", occupied.Message);
                    Assert.Equal(profile, File.ReadAllText(selected));
                    Assert.Null(source.Definitions.Read().Bindings.Single(item => item.Id == binding.Id).Source);
                }
                finally
                {
                    File.WriteAllText(Path.Combine(siblingDirectory, "nexus-editor-exit"), "");
                    await sibling.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            var session = Assert.IsType<ConfigurationEditStatus>((await editor.OpenAsync("editor", request, () => { }, default)).Session);
            Assert.NotNull((await controller.SaveBindingAsync(Guid.NewGuid(), "editor", session.SessionId,
                session.Revision, null, DateTimeOffset.UtcNow)).Saved);
            AttemptAcceptanceResult Start() => fixture.Service.Start(Guid.NewGuid(), "editor",
                source.Request(new BindingTarget(binding.Id)), DateTimeOffset.UtcNow);
            _ = Start();
            File.WriteAllText(duplicate, profile);
            var blocked = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(EngineOutcome.NotStarted, blocked.Facts.Engine);
            Assert.False(File.Exists(marker));
            Assert.Equal(profile, File.ReadAllText(duplicate));
            File.Delete(duplicate);

            var accepted = Start();
            var completed = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
            Assert.Equal(EngineOutcome.Succeeded, completed.Facts.Engine);
            Assert.Equal("Daily", File.ReadAllText(marker));
            var state = AttemptWriterState.Read(source.Instance, source.Database, contracts, accepted.JournalId);
            var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var blobs = new ImmutableBlobStore(source.Instance, contracts);
            var document = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
            Assert.NotNull(document.TaskAdapter);
            var observed = AdapterObservationCodec.Read(blobs, result.Binding, document.RawOutput!, Assert.Single(state.Total.Steps), document.TaskAdapter);
            Assert.Equal("bettergi:mail", Assert.Single(observed.Observations).TaskId);
        }
        finally { await controller.StopAsync(TimeSpan.FromSeconds(25)); }
    }

    private static async Task ExitEditorAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = Path.Combine(directory, "nexus-editor-started");
        while (!File.Exists(started) || new FileInfo(started).Length == 0) await Task.Delay(20, deadline.Token);
        var exit = Path.Combine(directory, "nexus-editor-exit");
        using var editor = Process.GetProcessById(int.Parse(File.ReadAllText(started), System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(exit, "");
        await editor.WaitForExitAsync(deadline.Token);
        File.Delete(exit);
    }

    private sealed class LateProfile(string primary) : IConfigurationEditObserver
    {
        private string? _late;
        internal void Arm(string path) => _late = path;
        public void Reached(ConfigurationEditBoundary boundary, Guid sessionId)
        {
            if (boundary != ConfigurationEditBoundary.WriterStopped || _late is not { } path) return;
            Assert.True(File.Exists(primary));
            _late = null;
            File.WriteAllText(path, "{\"Name\":\"X\"}");
        }
    }
}
