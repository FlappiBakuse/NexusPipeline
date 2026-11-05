using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationWriterTests
{
    private const string Subject = "execution-joint-writer";

    [Theory(DisplayName = "One Actual Job Supplies Both The Parent Attempt And Configuration Closures While Restoring Originals And Saving Real Output")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharesOwnedClosure(bool child)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var report = ConfigureNativeWriter(source, child); var original = File.ReadAllBytes(source.Configuration);
        var operation = Guid.NewGuid();
        var receipt = fixture.Service.Start(operation, Subject, source.Request(new BindingTarget(source.BindingB)), PlanBuilderFixture.CreatedAt);
        using var owned = fixture.Take(operation);
        var step = Assert.Single(owned.Prepared.Total.Plan.ReadWire().Steps);
        var prepared = Assert.Single(owned.Prepared.Configurations).Value;
        var runs = new ConfigurationRunStore(source.Instance, source.Database, new());
        var sessionId = Guid.NewGuid();
        var session = runs.Begin(ConfigurationRunStore.BeginIdentity(sessionId, Subject, receipt.JournalId, step.StepId, prepared),
            receipt.JournalId, step.StepId, prepared, DateTimeOffset.UtcNow);
        var parent = new StepWriterScope(source.Instance, source.Database, new(), source.Lease, receipt.JournalId, step.StepId);
        var controller = new ConfigurationRunController(source.Instance, source.Database, new(), source.Lease, fixture.Index,
            sessionId, Subject, prepared, parent);
        var completed = false;
        try
        {
            parent.EnterApplying();
            var identity = controller.Start();
            var ready = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal(identity.ProcessId, ready.GetProperty("processId").GetInt32());
            var expectedFamily = new List<int> { identity.ProcessId };
            if (child) expectedFamily.Add((await OwnedCrashProcess.ReadReportAsync(report + ".child")).GetProperty("processId").GetInt32());
            Assert.NotEqual(original, File.ReadAllBytes(source.Configuration));
            Assert.Null(await controller.AwaitRootAsync(TimeSpan.FromMilliseconds(100)));
            await controller.FinishAsync(Guid.NewGuid(), interrupted: false);
            completed = true;
            Assert.Equal(original, File.ReadAllBytes(source.Configuration));
            Assert.Equal(ConfigurationRunPhase.Completed, runs.Read(sessionId, Subject)!.Phase);
            var writer = Assert.Single(parent.VerifyReadOnly().Steps);
            var closures = new JobClosureStore(source.Instance, source.Database, new());
            var parentClosure = closures.Read(receipt.JournalId, writer.WriterId, writer.JobName, writer.SessionId)!;
            var resource = Assert.Single(session.Resources);
            var configurationClosure = closures.Read(resource.JournalId, writer.WriterId, writer.JobName, writer.SessionId)!;
            Assert.Equal(operation, parentClosure.OperationId); Assert.Equal(sessionId, configurationClosure.OperationId);
            Assert.Equal(new[] { receipt.JournalId }, parentClosure.JournalIds);
            Assert.Equal(new[] { resource.JournalId }, configurationClosure.JournalIds);
            Assert.True(parentClosure.WasExecuted); Assert.True(configurationClosure.WasExecuted);
            Assert.Equal(parentClosure.Root, configurationClosure.Root);
            Assert.Equal(parentClosure.Family.ToArray(), configurationClosure.Family.ToArray());
            Assert.All(expectedFamily, id => Assert.Contains(parentClosure.Family, member => member.ProcessId == id));
            Assert.Equal(parentClosure.Family.Length, parentClosure.Family.Select(member => (member.ProcessId, member.CreatedAtUtc)).Distinct().Count());
            Assert.All(parentClosure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.Equal(parentClosure.RunElapsedMilliseconds, configurationClosure.RunElapsedMilliseconds);
            var saved = new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(resource.Address)!;
            Assert.Equal(2, saved.Revision);
            var content = Assert.Single(saved.Tree.Entries, entry => entry.Kind == FileObjectKind.File);
            Assert.Equal("{\"fixture\":false}", File.ReadAllText(Path.Combine(source.Instance.RuntimeRoot, $"blobs/content/{content.Sha256}.bin")));
            Assert.Equal(JournalPhase.Applying, parent.VerifyReadOnly().Journal.Phase);
            Assert.Contains(fixture.Index.Current.Isolations, isolation => isolation.JournalId == receipt.JournalId);
        }
        finally
        {
            if (completed) controller.Dispose();
            else
            {
                try { await controller.StopWriterAsync(TimeSpan.FromSeconds(10)); }
                catch (IOException) { }
            }
        }
    }

    private static string ConfigureNativeWriter(PlanBuilderFixture source, bool child)
    {
        var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var input = Path.Combine(source.Root, "joint-writer-input.json"); var report = Path.Combine(source.Root, "joint-writer-ready.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { child,
            operations = new[] { new { target = source.Configuration, kind = "file", value = "{\"fixture\":false}" } } }));
        var catalog = source.Definitions.Read();
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Native fixture", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var script = catalog.Scripts.Single(script => script.Id == source.ScriptId) with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["configuration-editor-writer", input, report]
        };
        var change = new AutomationChanges(catalog.Revision, [root], [script], [], [], [], [], []);
        source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, change),
            change, DateTimeOffset.UtcNow);
        return report;
    }
}
