using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class UnclosedRecoveryTests
{
    [Theory(DisplayName = "A Dead Execution Owner With An Unclosed Real Worker Recovers Into Durable Isolation Without Replaying Or Inventing Completion")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatesLostReceipts(bool retainJob)
    {
        using var source = new PlanBuilderFixture();
        var original = File.ReadAllBytes(source.Configuration);
        var catalog = source.Definitions.Read(); var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var root = catalog.Roots.Single(item => item.Id == script.RootId);
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var marker = Path.Combine(source.Root, "unclosed-worker");
        var changes = new AutomationChanges(catalog.Revision, [root with { Path = Path.GetDirectoryName(executable)! }],
            [script with { Executable = new(root.Id, Path.GetFileName(executable)), Arguments = ["process-output", "tail", marker] }],
            [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "recovery-test", changes), changes, PlanBuilderFixture.CreatedAt);
        var request = source.Request(new BindingTarget(source.OtherBinding)); var operationId = Guid.NewGuid();
        var input = Path.Combine(source.Root, "unclosed-input.json"); var reportPath = input + ".report";
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = source.Instance.RuntimeRoot,
            point = "unclosed", bindingId = source.OtherBinding, catalogRevision = request.ExpectedCatalogRevision,
            settingsRevision = request.ExpectedSettingsRevision, operationId, subject = "recovery-test" }));
        source.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        var owned = owner.Start(["execution-terminal-recovery", input, reportPath], source.Root, source.Instance.InstanceId,
            operationId, input + ".owner");
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        Assert.Equal(owned.ProcessId, report.GetProperty("ownerProcessId").GetInt32());
        var journalId = report.GetProperty("journalId").GetGuid();
        var binding = report.GetProperty("binding").Deserialize<StepEvidenceBinding>()!;
        using var retained = retainJob ? JobNative.OpenJobObjectW(4, false, binding.JobName) : null;
        if (retainJob) Assert.False(retained!.IsInvalid);
        Assert.ThrowsAny<IOException>(() => InstanceLease.Acquire(source.Instance, new()));
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(input + ".stopped", JsonSerializer.SerializeToUtf8Bytes(stopped));
        using var lease = InstanceLease.Acquire(source.Instance, new());
        var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "recovery-inspection"),
            new JournalStartupAdmission(source.Instance, new()));
        var roots = new PayloadApplyRoots(Path.Combine(source.Root, "host"), Path.Combine(source.Instance.RuntimeRoot, "extensions"));
        var index = new StartupJournalRecovery(source.Instance, new(), database, roots).Recover(lease);
        Assert.True(index.Current.IsReady); Assert.NotEmpty(index.Current.Isolations);
        Assert.Equal(JournalPhase.Quarantined, new RecoveryJournalRegistry(database).ReadJournal(journalId).Phase);
        Assert.Null(new AttemptReceiptStore(database).Read(binding.AttemptId));
        Assert.Null(new JobClosureStore(source.Instance, database, new()).ReadForExecutionEvidence(journalId,
            binding.WriterId, binding.JobName, binding.SessionId));
        Assert.Equal(original, File.ReadAllBytes(source.Configuration));
        var before = new JournalStore(source.Instance, new()).ReadAllRevisions(journalId).Select(item => item.Export()).ToArray();
        _ = new StartupJournalRecovery(source.Instance, new(), database, roots).Recover(lease);
        var after = new JournalStore(source.Instance, new()).ReadAllRevisions(journalId).Select(item => item.Export()).ToArray();
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
    }
}
