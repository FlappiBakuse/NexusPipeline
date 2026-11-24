using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderConsumptionTests
{
    [Fact(DisplayName = "Prepared Provider Protects Its Parsed Installation Bytes Until Release And Rechecks Live Journal Inventory And Sql State")]
    public async Task HoldsInstallationEvidence()
    {
        await using var fixture = new InstalledProviderFixture();
        await fixture.PublishAsync(); await fixture.CommitPreparationAsync();
        var installation = new ExtensionInstalledEvidence(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Roots)
            .ReadCurrent("fixture-plugin")!;
        var metadata = Path.Combine(fixture.Source.Instance.RuntimeRoot, installation.Metadata.Footprint.RelativePath);
        var bytes = File.ReadAllBytes(metadata);
        var journal = Path.Combine(fixture.Source.Instance.RuntimeRoot, "journals", fixture.Accepted.JournalId.ToString("D"));
        var revision = Path.Combine(journal, "0000000001.json");
        var original = File.ReadAllBytes(revision);
        using (var provider = fixture.Providers.Acquire("fixture-plugin"))
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(metadata, bytes));
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(revision, original));
            Assert.Equal("fixture-plugin", provider.Use(package => package.Manifest.ExtensionId));
            var sibling = Path.Combine(journal, "9999999999.json");
            File.WriteAllText(sibling, "{}");
            try { Assert.ThrowsAny<IOException>(() => provider.Use(package => package.Manifest.ExtensionId)); }
            finally { File.Delete(sibling); }
            using var connection = DatabaseConnections.Open(fixture.Source.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE main.operation_journals SET phase=$phase WHERE journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", fixture.Accepted.JournalId.ToString("D"));
            var phase = command.Parameters.AddWithValue("$phase", "applied");
            command.ExecuteNonQuery();
            try { Assert.ThrowsAny<IOException>(() => provider.Use(package => package.Manifest.ExtensionId)); }
            finally { phase.Value = "committed"; command.ExecuteNonQuery(); }
            Assert.Equal("fixture-plugin", provider.Use(package => package.Manifest.ExtensionId));
        }
        File.WriteAllBytes(metadata, bytes); File.WriteAllBytes(revision, original);
        using var reopened = fixture.Providers.Acquire("fixture-plugin");
        Assert.Equal("fixture-plugin", reopened.Use(package => package.Manifest.ExtensionId));
    }

    [Fact(DisplayName = "A Real Published Provider Cannot Consume Before Final Commit And Runs After Preparation Is Disposed")]
    public async Task RequiresFinalPublication()
    {
        await using var fixture = new InstalledProviderFixture();
        await fixture.PublishAsync();
        var archive = fixture.ArchivePath;
        var before = fixture.Execution.Count("attempt_receipts");
        Assert.Equal("extension_installation_consumption_unavailable",
            Assert.Throws<IOException>(() => fixture.Providers.Acquire("fixture-plugin")).Message);
        using (var draft = fixture.Service.DryRun(fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)),
            DateTimeOffset.UtcNow))
        {
            Assert.Null(draft.PlanDigest);
            Assert.Equal("execution_input_unavailable", Assert.Single(draft.Explanation.Items).ReasonCode);
        }
        Assert.Equal("execution_no_runnable_targets", Assert.Throws<ExecutionSelectionException>(() => fixture.Start()).Message);
        Assert.Null(fixture.Execution.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease);
        Assert.Equal(before, fixture.Execution.Count("attempt_receipts"));
        Assert.Equal(0, fixture.Execution.Inbox.PendingCount); Assert.False(File.Exists(fixture.Marker));
        await fixture.CommitPreparationAsync();
        using (var released = File.Open(archive, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        using (var actual = fixture.Providers.Acquire("fixture-plugin"))
            Assert.Equal("fixture-plugin", actual.Use(package => package.Manifest.ExtensionId));
        var accepted = fixture.Start();
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.True(File.Exists(fixture.Marker));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(result.Binding, terminal.Binding);
        var closure = new JobClosureStore(fixture.Source.Instance, fixture.Source.Database, new())
            .ReadForExecutionEvidence(result.Binding.ParentJournalId, result.Binding.WriterId,
                result.Binding.JobName, result.Binding.SessionId);
        Assert.NotNull(closure); Assert.True(closure.WasExecuted); Assert.NotEmpty(closure.Family);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
    }

    [Fact(DisplayName = "Current Disabled Preference Rejects An Already Prepared Installed Provider Without Rewriting Its Committed Fact")]
    public async Task RechecksDisabledPreference()
    {
        await using var fixture = new InstalledProviderFixture();
        await fixture.PublishAsync(); await fixture.CommitPreparationAsync();
        using var provider = fixture.Providers.Acquire("fixture-plugin");
        using (var root = fixture.Execution.Resources.TryAcquire(Guid.NewGuid(),
            [new("settings:host", ResourceClaimMode.Mutation)]).Lease!)
        {
            Assert.NotNull(root);
            var store = fixture.Source.Settings; var before = store.Read();
            var change = new HostSettingsChange(before.Revision,
                before.Values with { Extensions = [new("fixture-plugin", false)] });
            store.Commit(HostSettingsStore.Identity(root.OperationId, "local", change), change, DateTimeOffset.UtcNow);
        }
        Assert.Equal("extension_installation_consumption_unavailable",
            Assert.Throws<IOException>(() => provider.Use(package => package.Manifest.ExtensionId)).Message);
        Assert.Equal("extension_installation_consumption_unavailable",
            Assert.Throws<IOException>(() => fixture.Providers.Acquire("fixture-plugin")).Message);
        using (var draft = fixture.Service.DryRun(fixture.Source.Request(new BindingTarget(fixture.Source.OtherBinding)),
            DateTimeOffset.UtcNow))
        {
            Assert.Null(draft.PlanDigest);
            Assert.Equal("execution_extension_disabled", Assert.Single(draft.Explanation.Items).ReasonCode);
        }
        Assert.Equal("execution_no_enabled_bindings", Assert.Throws<ExecutionSelectionException>(() => fixture.Start()).Message);
        Assert.Equal(0, fixture.Execution.Inbox.PendingCount); Assert.False(File.Exists(fixture.Marker));
        Assert.Equal(JournalPhase.Committed, new RecoveryJournalRegistry(fixture.Source.Database)
            .ReadJournal(fixture.Accepted.JournalId).Phase);
    }

    [Theory(DisplayName = "Provider Protection Survives The Activation Callback And Releases After Rejection")]
    [InlineData("namespace")]
    [InlineData("callback")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public async Task ProtectsPendingActivation(string failure)
    {
        await using var fixture = new InstalledProviderFixture(failure == "callback" ? "late-throw" : "late");
        using var cancellation = new CancellationTokenSource();
        var running = fixture.PublishAsync(cancellation.Token);
        var release = Path.Combine(fixture.ActivationDirectory, "release");
        string? archive = null, unexpected = null, worker = null;
        Exception? error = null;
        Task? stopping = null;
        try
        {
            _ = await OwnedCrashProcess.ReadReportAsync(Path.Combine(fixture.ActivationDirectory, "activated.json"));
            var protectedArchive = fixture.ArchivePath; archive = protectedArchive;
            var workerDirectory = Path.Combine(fixture.TargetPath, "worker");
            worker = Path.Combine(workerDirectory, Path.GetFileName(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!));
            Assert.Throws<IOException>(() => { using var writer = File.Open(protectedArchive, FileMode.Open, FileAccess.Write, FileShare.Read); });
            Assert.Throws<IOException>(() => File.Delete(worker));
            Assert.Throws<IOException>(() => Directory.Move(workerDirectory, workerDirectory + "-moved"));
            if (failure == "namespace")
            {
                unexpected = Path.Combine(fixture.TargetPath, "unexpected.txt");
                File.WriteAllText(unexpected, "preserve");
            }
            if (failure == "cancel") cancellation.Cancel();
            if (failure == "stop")
            {
                stopping = fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
                Assert.False(stopping.IsCompleted);
            }
            Assert.False(running.IsCompleted);
            Assert.Throws<IOException>(() => { using var writer = File.Open(protectedArchive, FileMode.Open, FileAccess.Write, FileShare.Read); });
        }
        finally
        {
            File.WriteAllText(release, "release");
            error = await Record.ExceptionAsync(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.True(running.IsCompleted);
        switch (failure)
        {
            case "namespace":
                Assert.Equal("execution_provider_package_changed", Assert.IsType<IOException>(error).Message);
                break;
            case "callback":
                Assert.Equal("controlled_activation_failure", Assert.IsType<InvalidOperationException>(error).Message);
                break;
            case "cancel":
                Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                break;
            case "stop":
                Assert.False(cancellation.IsCancellationRequested);
                Assert.True(Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken.IsCancellationRequested);
                break;
        }
        if (stopping is not null) await stopping;
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        Assert.Throws<IOException>(() => fixture.Providers.Acquire("fixture-plugin"));
        if (unexpected is not null) { Assert.Equal("preserve", File.ReadAllText(unexpected)); File.Delete(unexpected); }
        using (File.Open(archive!, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        using (File.Open(worker!, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
        var releasedDirectory = Path.GetDirectoryName(worker!)!;
        var identity = PhysicalObjectIdentity.Read(releasedDirectory);
        var relocated = releasedDirectory + "-release-check";
        Directory.Move(releasedDirectory, relocated);
        try { Assert.Equal(identity, PhysicalObjectIdentity.Read(relocated)); }
        finally { Directory.Move(relocated, releasedDirectory); }
        Assert.Equal(identity, PhysicalObjectIdentity.Read(releasedDirectory));
    }
}
