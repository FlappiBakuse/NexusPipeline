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
    [Fact(DisplayName = "Prepared Input Retains Its Log And Bounds The First And Last Admitted Generations")]
    public void ReservesInputGenerations()
    {
        var primary = new StepEvidenceBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "binding-" + Guid.NewGuid().ToString("D"), "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 1, new string('a', 64));
        var identity = new JudgementWorkerIdentity(primary, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid());
        JudgementInputDraft draft;
        using (var parsed = JsonDocument.Parse("{\"generation\":4095,\"isFinal\":false,\"log\":\"saved 雪\\n\",\"files\":[\"variable\"],\"locale\":\"zh-CN\",\"screenshots\":[]}"))
            draft = new(identity, parsed.RootElement);
        var first = new JudgementInvocation(primary.AttemptId, 1, false, identity.OwnerId);
        foreach (var call in new[] { first, first with { Generation = 4095 }, first with { Generation = 4095, IsFinal = true } })
        {
            var frame = JudgementWorkerProtocol.Encode(draft.Bind(call) with { ProbeEvidenceAllowance = JudgementProbeProtocol.MaximumEvidenceBytes });
            Assert.True(frame.LongLength <= draft.MaximumEncodedBytes);
            var saved = JudgementWorkerProtocol.Decode<JudgementWorkerInvocation>(frame);
            Assert.Equal((call.Generation, call.IsFinal), (saved.Generation, saved.IsFinal));
            Assert.Equal((call.Generation, call.IsFinal), (saved.Input.GetProperty("generation").GetInt64(), saved.Input.GetProperty("isFinal").GetBoolean()));
            Assert.Equal("saved 雪\n", saved.Input.GetProperty("log").GetString());
        }
        var minimumInput = JsonSerializer.SerializeToElement(new { generation = 1, isFinal = false, log = "saved 雪\n",
            files = Array.Empty<object>(), locale = "", screenshots = Array.Empty<object>() });
        var minimum = new JudgementWorkerInvocation(JudgementWorkerProtocol.InvocationFormat, 1, identity, 1, false,
            minimumInput, JudgementWorkerProtocol.InputHash(minimumInput)) { ProbeEvidenceAllowance = JudgementProbeProtocol.FaultReserveBytes };
        Assert.True(draft.StableMinimumEncodedBytes <= JudgementWorkerProtocol.Encode(minimum).LongLength);
        Assert.Throws<IOException>(() => draft.Bind(first with { OwnerId = Guid.NewGuid() }));
    }

    [Fact(DisplayName = "Script Preview Binds Its Source And Broker Without Persisting Inputs")]
    public void FreezesPreviewAssets()
    {
        using var source = new PlanBuilderFixture(); SetScript(source, AutomationJudgementLanguage.JavaScript);
        var before = source.Snapshot();
        using var preview = Builder(source).Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        var plan = Assert.IsType<FrozenExecutionPlan>(preview.ReadPlan()).ReadWire(); var worker = Assert.Single(plan.Steps).JudgementWorker!;
        Assert.NotNull(worker); Assert.Null(worker.Python);
        Assert.Equal(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE"), worker.Broker.Path);
        Assert.Equal(JudgementWorkerProtocol.Hash(Encoding.UTF8.GetBytes("globalThis.nexusJudge = () => ({outcome:'unverified',reason:''});")), worker.ScriptSha256);
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
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
        Assert.ThrowsAny<IOException>(() => builder.Freeze(preview));
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
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
        var codec = new ExecutionPlanCodec(source.Instance, new(), new(source.Instance, new()));
        var historical = codec.Read(reference).ReadWire();
        Assert.Equal(broker, Assert.Single(historical.Steps).JudgementWorker!.Broker.Path);
        Assert.ThrowsAny<IOException>(() => codec.Freeze(historical));
    }

    [Fact(DisplayName = "A Queue That Fits For Primary Jobs Alone Can Exceed Capacity With Judgement Jobs")]
    public void BoundsNativeClosures()
    {
        using var source = new PlanBuilderFixture(); SetScript(source, AutomationJudgementLanguage.JavaScript);
        using var preview = Builder(source).Prepare(source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        var plan = preview.ReadPlan()!.ReadWire(); var step = Assert.Single(plan.Steps);
        var steps = Enumerable.Range(0, 48).Select(index => step with { StepId = $"step-{index:D2}", MaximumAttempts = 1 }).ToArray();
        var primary = steps.Select(item => item with { JudgementWorker = null }).ToArray();
        var admitted = Enumerable.Range(1, primary.Length).Reverse().Select(count => plan with { Steps = primary[..count] })
            .First(candidate => JournalCapacityPolicy.Measure(candidate).Fits);
        JournalCapacityPolicy.Require(plan);
        JournalCapacityPolicy.Require(admitted);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() =>
            JournalCapacityPolicy.Require(plan with { Steps = steps[..admitted.Steps.Count] })).Message);
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
