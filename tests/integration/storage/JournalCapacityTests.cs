using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JournalCapacityTests
{
    [Fact(DisplayName = "An Oversized Queue Is Blocked By Both Preview And Start Without Persisting An Attempt")]
    public void RejectsOversizedQueues()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source; var catalog = source.Definitions.Read();
        var users = Enumerable.Range(0, 160).Select(index => new AutomationUser(Guid.NewGuid(), 0, "Capacity " + index, index)).ToImmutableArray();
        var bindings = users.Select(user => new AutomationBinding(Guid.NewGuid(), 0, user.Id, source.OtherScriptId, true, 0)).ToImmutableArray();
        var change = new AutomationChanges(catalog.Revision, [], [], users, bindings,
            [catalog.Queues.Single() with { ScriptIds = [source.OtherScriptId] }], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "capacity-fixture", change), change, DateTimeOffset.UtcNow);
        var request = source.Request(new QueueTarget(source.QueueId)); var before = source.Snapshot();
        using var preview = fixture.Service.DryRun(request, DateTimeOffset.UtcNow);
        Assert.Null(preview.ReadPlan()); Assert.False(preview.Explanation.HasRunnable); Assert.True(preview.Explanation.HasBlockedItems);
        Assert.All(preview.Explanation.Items, item => Assert.Equal(JournalCapacityPolicy.Reason, item.ReasonCode));
        Assert.Throws<ExecutionSelectionException>(() => fixture.Service.Start(Guid.NewGuid(), "capacity-fixture", request, DateTimeOffset.UtcNow));
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.Equal(0, fixture.Count("attempt_receipts")); Assert.Equal(0, fixture.Inbox.PendingCount);
    }

    [Fact(DisplayName = "The History Limit Rejects A Plan Before Its Final Document Or Participant Limits Would Be Exceeded")]
    public void BoundsHistoryDocuments()
    {
        var fixture = ExecutionPlanFixture.Create();
        var plan = fixture.Plan with { Steps = Enumerable.Range(0, 160).Select(index => fixture.Plan.Steps[0] with { StepId = "step-" + index, MaximumAttempts = 1 }).ToArray() };
        var capacity = JournalCapacityPolicy.Measure(plan);
        Assert.True(capacity.MaximumDocumentBytes < StrictJson.MaximumBytes); Assert.True(capacity.Participants < JournalCapacityLimits.MaximumParticipants);
        Assert.True(capacity.Revisions < JournalCapacityLimits.MaximumRevisions); Assert.True(capacity.HistoryBytes > JournalCapacityLimits.MaximumHistoryBytes);
        var error = Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(plan));
        Assert.Equal(JournalCapacityPolicy.Reason, error.Message);
    }

    [Fact(DisplayName = "Encoding Width Configuration Groups And The Uncreated Suffix Are Included In The Capacity Bound")]
    public void CountsUncreatedSuffix()
    {
        var fixture = ExecutionPlanFixture.CreateManagedPaths();
        var ordinary = JournalCapacityPolicy.Measure(fixture.Plan);
        var wide = JournalCapacityPolicy.Measure(fixture.Plan with
        {
            Steps = [fixture.Plan.Steps[0] with { StepId = new string('界', 120) }],
            ConfigurationSlots = fixture.Plan.ConfigurationSlots.Select(slot => slot with { StepId = new string('界', 120) }).ToArray()
        });
        var noConfiguration = JournalCapacityPolicy.Measure(fixture.Plan with { ConfigurationSlots = [] });
        Assert.True(ordinary.Fits); Assert.True(wide.MaximumDocumentBytes > ordinary.MaximumDocumentBytes);
        var invocations = fixture.Plan.Steps.Single().MaximumAttempts ?? 1;
        Assert.True(wide.HistoryBytes > ordinary.HistoryBytes); Assert.Equal(noConfiguration.Participants + invocations, ordinary.Participants);
        Assert.Equal(noConfiguration.Receipts + invocations, ordinary.Receipts); Assert.Equal(noConfiguration.Revisions + invocations, ordinary.Revisions);
        Assert.True(ordinary.Participants >= 5 + 3); Assert.True(ordinary.Receipts >= 3);
    }

    [Fact(DisplayName = "Frozen Owned Targets Reserve Every Bounded Retention Revision Before Admission")]
    public void ReservesTargetRetention()
    {
        var fixture = ExecutionPlanFixture.Create();
        var ordinary = JournalCapacityPolicy.Measure(fixture.Plan);
        var withTarget = JournalCapacityPolicy.Measure(WithPcTarget(fixture.Plan));
        Assert.True(withTarget.Fits);
        Assert.True(withTarget.MaximumDocumentBytes > ordinary.MaximumDocumentBytes);
        Assert.True(withTarget.HistoryBytes > ordinary.HistoryBytes);
        var invocations = fixture.Plan.Steps.Single().MaximumAttempts ?? 1;
        Assert.Equal(ordinary.Participants + invocations * (5 + TargetJobCodec.MaximumRetentionIntents), withTarget.Participants);
        Assert.Equal(ordinary.Revisions + invocations * (5 + TargetJobCodec.MaximumRetentionIntents), withTarget.Revisions);
        Assert.Equal(ordinary.Jobs + invocations, withTarget.Jobs);
    }

    [Theory(DisplayName = "Target Journal Decoding Rejects An Unbounded Retention History Even With Valid Stage Transitions")]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void RejectsUnboundedRetention(int intents, bool accepted)
    {
        var plan = WithPcTarget(ExecutionPlanFixture.Create().Plan);
        var binding = new ExecutionTargetBinding(Guid.Parse(plan.InstanceId), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            plan.Steps[0].StepId, Guid.NewGuid(), "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 1, new('a', 64));
        var participants = new JsonArray();
        foreach (var stage in new[] { "registered", "root", "ready" }.Concat(Enumerable.Repeat("retentionPrepared", intents)).Append("recoveredRetained"))
            participants.Add(TargetJobCodec.Participant(new(TargetJobCodec.Format, 1, binding, stage,
                stage == "registered" ? null : new("blobs/plans/" + new string('b', 64) + ".json", 1, new('b', 64))), plan.Steps[0].ScriptId));
        var document = new JsonObject
        {
            ["journalId"] = binding.ParentJournalId.ToString("D"), ["operationId"] = binding.ParentOperationId.ToString("D"),
            ["attemptId"] = binding.AttemptId.ToString("D"), ["participants"] = participants
        };
        using var parsed = JsonDocument.Parse(document.ToJsonString());
        if (accepted) Assert.Equal("recoveredRetained", Assert.Single(TargetJobCodec.Read(parsed.RootElement, plan, binding.TotalPlanSha256)).Stage);
        else Assert.Throws<JsonException>(() => TargetJobCodec.Read(parsed.RootElement, plan, binding.TotalPlanSha256));
    }

    [Fact(DisplayName = "Recovery Capacity Reserves Every Frozen Configuration Invocation And Repeated Job Membership")]
    public void ReservesConfigurationLifecycle()
    {
        var fixture = ExecutionPlanFixture.CreateManagedPaths();
        var first = fixture.Plan.Steps[0] with { MaximumAttempts = 3 };
        var second = first with { StepId = "second", MaximumAttempts = 2 };
        var slot = fixture.Plan.ConfigurationSlots[0];
        var plan = fixture.Plan with
        {
            Steps = [first, second],
            ConfigurationSlots = [slot, slot with { ResourceId = "other", TargetPath = slot.TargetPath + "-other" },
                slot with { StepId = second.StepId }]
        };
        var capacity = ExecutionRecoveryCapacity.Measure(plan);
        Assert.Equal(9, capacity.Journals);
        Assert.Equal(32, capacity.FileEffects);
        Assert.Equal(15, capacity.Jobs);
        var plain = ExecutionRecoveryCapacity.Measure(plan with { ConfigurationSlots = [] });
        Assert.Equal(1, plain.Journals); Assert.Equal(0, plain.FileEffects); Assert.Equal(5, plain.Jobs);
    }

    [Fact(DisplayName = "Admission Reserves Both Full Width Task Id Arrays And Previous Evidence For Every Adapter Retry")]
    public void ReservesTaskRetry()
    {
        var fixture = ExecutionPlanFixture.Create(); var step = fixture.Plan.Steps[0];
        var tasks = Enumerable.Range(0, 4096).Select(index => step.Tasks[0] with
            { TaskId = new string('界', 115) + index.ToString("D4") }).ToArray();
        var reference = fixture.Plan.DefinitionBundle;
        var adapter = new NexusPipeline.Platform.Serialization.Protocol.ExecutionAdapter
        {
            InstalledRevision = 1, InstalledJournalId = Guid.NewGuid().ToString("D"),
            InstalledFootprint = new() { RelativePath = reference.RelativePath, SizeBytes = reference.SizeBytes, Sha256 = reference.Sha256 },
            Manifest = reference, Script = reference, Resources = []
        };
        var plan = fixture.Plan with { Steps = [step with { Tasks = tasks, MaximumAttempts = 2, Adapter = adapter,
            SelectedTaskIds = tasks.Select(task => task.TaskId).ToArray() }] };
        var plain = JournalCapacityPolicy.Measure(plan with { Steps = [plan.Steps[0] with { Adapter = null }] });
        var reserved = JournalCapacityPolicy.Measure(plan);
        Assert.True(plain.Fits); Assert.False(reserved.Fits);
        Assert.True(reserved.MaximumDocumentBytes > StrictJson.MaximumBytes);
        Assert.Equal(plain.Participants, reserved.Participants); Assert.Equal(plain.Revisions, reserved.Revisions);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(plan)).Message);
    }

    private static ExecutionPlan WithPcTarget(ExecutionPlan plan)
    {
        var step = plan.Steps[0];
        var image = new ExecutionPlanInterpreterImage { Path = step.ExecutablePath, SizeBytes = step.ImageSizeBytes, Sha256 = step.ImageSha256,
            Identity = step.ImageIdentity, ParentIdentity = step.ParentIdentity, RuntimeFiles = [] };
        var target = new NexusPipeline.Platform.Serialization.Protocol.ExecutionTarget { Kind = "pc", LaunchByHost = true, WaitSeconds = 1, ForceCloseAtEnd = false, Pc = new()
        {
            Launch = new() { LaunchKind = "native", Source = image, Command = image, SourceArguments = [], Arguments = [],
                WorkingDirectory = step.WorkingDirectory, WorkingDirectoryIdentity = step.WorkingDirectoryIdentity },
            ReadyImage = image, ReadinessMode = "process"
        } };
        return plan with { Steps = [step with { Target = target }] };
    }
}
