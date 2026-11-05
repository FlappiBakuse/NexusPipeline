using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementInputsTests
{
    [Fact(DisplayName = "Prepared Input Bounds Every Admitted Generation Without Allocating The Core Call Or Resampling Its Source")]
    public void ReservesInputGenerations()
    {
        var primary = new StepEvidenceBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "binding-" + Guid.NewGuid().ToString("D"), "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 1, new string('a', 64));
        var core = new AttemptBusinessJudgement(primary.AttemptId, new(AutomationJudgementLanguage.JavaScript, [], [], "source"));
        var identity = new JudgementWorkerIdentity(primary, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), core.InputIdentity.OwnerId);
        using var parsed = JsonDocument.Parse("{\"generation\":4095,\"isFinal\":false,\"log\":\"saved 雪\\n\",\"files\":[\"variable\"],\"locale\":\"zh-CN\",\"screenshots\":[]}");
        var draft = new JudgementInputDraft(identity, parsed.RootElement);
        Assert.False(core.IsBusy); var first = Assert.IsType<JudgementInvocation>(core.TryBeginPeriodic());
        Assert.Equal(1, first.Generation); var firstWire = draft.Bind(first); Assert.Equal("saved 雪\n", firstWire.Input.GetProperty("log").GetString());
        Assert.True(JudgementWorkerProtocol.Encode(firstWire).LongLength <= draft.MaximumEncodedBytes);
        var minimumInput = JsonSerializer.SerializeToElement(new { generation = 1, isFinal = false, log = "saved 雪\n",
            files = Array.Empty<object>(), locale = "", screenshots = Array.Empty<object>() });
        var minimum = firstWire with { Input = minimumInput, InputSha256 = JudgementWorkerProtocol.InputHash(minimumInput),
            ProbeEvidenceAllowance = JudgementProbeProtocol.FaultReserveBytes };
        Assert.Equal(JudgementWorkerProtocol.Encode(minimum).LongLength, draft.StableMinimumEncodedBytes);
        Assert.Equal(JudgementWorkerProtocol.Hash(JsonSerializer.SerializeToUtf8Bytes(firstWire.Input)), firstWire.InputSha256);
        var decoded = JudgementWorkerProtocol.Decode<JudgementWorkerInvocation>(JudgementWorkerProtocol.Encode(firstWire));
        Assert.True(JsonElement.DeepEquals(firstWire.Input, decoded.Input));
        Assert.False(core.Complete(first, new(BusinessOutcome.Unverified, ""))); core.RequestFinal();
        var final = Assert.IsType<JudgementInvocation>(core.TryBeginFinal()); var finalWire = draft.Bind(final);
        Assert.Equal(2, final.Generation); Assert.True(finalWire.Input.GetProperty("isFinal").GetBoolean());
        Assert.True(JudgementWorkerProtocol.Encode(finalWire).LongLength <= draft.MaximumEncodedBytes);
        Assert.True(JudgementWorkerProtocol.Encode(draft.Bind(final with { Generation = 4095, IsFinal = false })).LongLength <= draft.MaximumEncodedBytes);
        Assert.Throws<IOException>(() => draft.Bind(final with { OwnerId = Guid.NewGuid() }));
    }

    [Fact(DisplayName = "Script Preview Freezes Source And Real Runtime Assets Without Persisting Any Input")]
    public void FreezesPreviewAssets()
    {
        using var source = new PlanBuilderFixture(); SetScript(source, AutomationJudgementLanguage.JavaScript);
        var before = source.Snapshot();
        using var preview = Builder(source).Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        var plan = Assert.IsType<FrozenExecutionPlan>(preview.ReadPlan()).ReadWire(); var worker = Assert.Single(plan.Steps).JudgementWorker!;
        Assert.NotNull(worker); Assert.Null(worker.Python);
        Assert.Equal(JudgementWorkerProtocol.Hash(Encoding.UTF8.GetBytes("globalThis.nexusJudge = () => ({outcome:'unverified',reason:''});")), worker.ScriptSha256);
        Assert.Contains(worker.Broker.RuntimeFiles, file => file.RelativePath == "Jint.dll");
        Assert.Contains(worker.Broker.RuntimeFiles, file => file.RelativePath == "NexusPipeline.Host.dll");
        Assert.Equal(before.Rows, source.Snapshot().Rows); Assert.Equal(before.Files, source.Snapshot().Files);
        using var missing = source.Builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        Assert.Null(missing.ReadPlan()); Assert.True(missing.Explanation.HasBlockedItems);
    }

    [Theory(DisplayName = "A Real Runtime Dependency Change After Preview Prevents Every Admission Write")]
    [InlineData("bytes")]
    [InlineData("identity")]
    [InlineData("missing")]
    public void RejectsChangedDependencies(string change)
    {
        using var source = new PlanBuilderFixture(); SetScript(source, AutomationJudgementLanguage.JavaScript);
        var broker = CopyBroker(source.Root); var builder = Builder(source, broker);
        using var preview = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        Assert.NotNull(preview.ReadPlan()); var dependency = Path.Combine(Path.GetDirectoryName(broker)!, "Jint.dll");
        if (change == "missing") File.Delete(dependency);
        else if (change == "identity")
        {
            File.Move(dependency, dependency + ".original"); File.Copy(dependency + ".original", dependency);
        }
        else File.WriteAllBytes(dependency, [1, 2, 3]);
        var before = source.Snapshot();
        Assert.IsAssignableFrom<IOException>(Record.Exception(() => builder.Freeze(preview)));
        Assert.Equal(before.Rows, source.Snapshot().Rows); Assert.Equal(before.Files, source.Snapshot().Files);
    }

    [Fact(DisplayName = "Historical Frozen Plans Remain Readable Without Opening Current Interpreter Dependencies")]
    public void ReadsFrozenHistory()
    {
        using var source = new PlanBuilderFixture(); SetScript(source, AutomationJudgementLanguage.JavaScript);
        var broker = CopyBroker(source.Root); var builder = Builder(source, broker);
        using var preview = builder.Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        BlobReference reference;
        using (var frozen = builder.Freeze(preview)) reference = frozen.Reference;
        File.Delete(Path.Combine(Path.GetDirectoryName(broker)!, "Jint.dll"));
        var historical = new ExecutionPlanCodec(source.Instance, new(), new(source.Instance, new())).Read(reference).ReadWire();
        Assert.Equal(broker, Assert.Single(historical.Steps).JudgementWorker!.Broker.Path);
        Assert.IsAssignableFrom<IOException>(Record.Exception(() => new ExecutionPlanCodec(source.Instance, new(), new(source.Instance, new())).Freeze(historical)));
    }

    [Fact(DisplayName = "Judgement Capacity Includes Both Native Role Closures Without Increasing Existing Limits")]
    public void BoundsNativeClosures()
    {
        using var source = new PlanBuilderFixture(); SetScript(source, AutomationJudgementLanguage.JavaScript);
        using var preview = Builder(source).Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        var plan = preview.ReadPlan()!.ReadWire(); var actual = JournalCapacityPolicy.Measure(plan);
        var primary = JournalCapacityPolicy.Measure(plan with { Steps = plan.Steps.Select(step => step with { JudgementWorker = null }).ToArray() });
        var invocations = plan.Steps.Sum(step => step.MaximumAttempts ?? 1);
        Assert.Equal(primary.Jobs + invocations, actual.Jobs); Assert.Equal(primary.Revisions + 2 * invocations, actual.Revisions);
        Assert.Equal(primary.Participants + 2 * invocations, actual.Participants); Assert.True(actual.Fits); Assert.True(actual.HistoryBytes > primary.HistoryBytes);
    }

    [Fact(DisplayName = "Attempt Log Tail Retains Four Mi B And Never Starts Inside A Utf 8 Character")]
    public void BoundsUtf8Tail()
    {
        var tail = new JudgementLogTail(); var line = new string('界', 4095);
        var count = JudgementWorkerProtocol.MaximumLogBytes / Encoding.UTF8.GetByteCount(line) + 2;
        for (var index = 0; index < count; index++) tail.AppendLine(line);
        var text = tail.Text(); Assert.True(tail.IsTruncated); Assert.EndsWith(line + "\n", text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), JudgementWorkerProtocol.MaximumLogBytes - 3, JudgementWorkerProtocol.MaximumLogBytes);
        Assert.DoesNotContain('\ufffd', text);
    }

    private static ExecutionPlanBuilder Builder(PlanBuilderFixture source, string? broker = null) => new(source.Instance, source.Database, new(), source.Lease,
        new(5, 6, 30), new(broker ?? Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, Environment.GetEnvironmentVariable("NEXUS_TEST_PYTHON_EXE")));

    private static void SetScript(PlanBuilderFixture source, AutomationJudgementLanguage language)
    {
        var catalog = source.Definitions.Read(); var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var changed = script with { Judgement = new(language, [], [], "globalThis.nexusJudge = () => ({outcome:'unverified',reason:''});") };
        var change = new AutomationChanges(catalog.Revision, [], [changed], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "judgement-input-fixture", change), change, PlanBuilderFixture.CreatedAt);
    }

    private static string CopyBroker(string ownedRoot)
    {
        var original = JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!);
        var directory = Path.Combine(ownedRoot, "broker"); Directory.CreateDirectory(directory);
        foreach (var relative in original.RuntimeFiles.Select(file => file.RelativePath).Append(Path.GetFileName(original.Path)))
        {
            var target = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(original.Path)!, relative), target);
        }
        return Path.Combine(directory, Path.GetFileName(original.Path));
    }
}
