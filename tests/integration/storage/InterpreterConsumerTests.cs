using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InterpreterConsumerTests
{
    [Theory(DisplayName = "Formal Consumer Runs Frozen Pre Main Post Interpreters And Restores The Shared Installed Configuration")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".com")]
    public async Task RunsFrozenHooks(string extension)
        => await RunConsumer(extension);

    [Fact(DisplayName = "Formal Consumer Runs Frozen Power Shell Hooks And Restores The Shared Installed Configuration")]
    public async Task RunsShellHooks()
        => await RunConsumer(".ps1");

    private static async Task RunConsumer(string extension)
    {
        using var fixture = new InterpretedHooksFixture(extension);
        var source = fixture.Source;
        var bytes = File.ReadAllBytes(source.Configuration); var original = PhysicalObjectIdentity.Read(source.Configuration);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync());
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle); Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var state = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.Accepted.JournalId);
        Assert.Equal(new[] { "preHook", "process", "postHook" }, state.Jobs.Select(job => job.Role));
        var results = HookResultCodec.Read(state.Latest.Root, state.Total); Assert.Equal(2, results.Length);
        foreach (var result in results)
        {
            var hook = state.Total.Steps.Single().Hooks!.Single(hook => hook.Role == result.Role);
            var job = state.Jobs.Single(job => job.Role == result.Role);
            var closure = Assert.IsType<FrozenJobClosure>(new JobClosureStore(source.Instance, source.Database, new())
                .ReadForExecutionEvidence(state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId));
            Assert.True(closure.WasExecuted); Assert.Equal(hook.Command.Path, closure.Root!.ImagePath, ignoreCase: true);
            var role = result.Role == "preHook" ? "pre" : "post";
            using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.Report(role)));
            Assert.Equal(fixture.WorkingDirectory, report.RootElement.GetProperty("workingDirectory").GetString());
            Assert.Contains(closure.Family, member => member.ProcessId == report.RootElement.GetProperty("processId").GetInt32());
            Assert.Contains(closure.Family, member => member.ProcessId == report.RootElement.GetProperty("childProcessId").GetInt32());
            if (extension == ".com") Assert.Contains(closure.Family, member =>
                member.ProcessId == report.RootElement.GetProperty("processId").GetInt32() && member.ImagePath == hook.Source.Path &&
                member.ImageSha256 == hook.Source.Sha256);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            using var script = extension == ".ps1" ? JsonDocument.Parse(File.ReadAllBytes(fixture.Report(role) + ".script")) : null;
            Assert.Equal(fixture.Values, (script?.RootElement ?? report.RootElement).GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
            var document = new HookEvidenceReader(source.Instance, source.Database, new()).Read(result.Binding, result.Role, result.Artifact);
            Assert.NotNull(document.RawOutput); Assert.Equal(EngineOutcome.Succeeded, result.Engine);
            var child = fixture.Base.Base.Count("journal_writer_jobs");
            Assert.True(child >= state.Jobs.Length);
        }
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(0, fixture.Base.Base.Count("resource_isolations")); Assert.True(fixture.Base.Base.Index.Current.IsReady);
    }
}
