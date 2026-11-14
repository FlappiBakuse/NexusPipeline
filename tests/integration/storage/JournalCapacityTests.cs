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
using NexusPipeline.Platform.Storage.Sqlite;
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
        var request = source.Request(new QueueTarget(source.QueueId));
        var before = new RecoveryJournalRegistry(source.Database).ReadActiveSnapshot();
        using var preview = fixture.Service.DryRun(request, DateTimeOffset.UtcNow);
        Assert.Null(preview.ReadPlan()); Assert.False(preview.Explanation.HasRunnable); Assert.True(preview.Explanation.HasBlockedItems);
        Assert.All(preview.Explanation.Items, item => Assert.Equal(JournalCapacityPolicy.Reason, item.ReasonCode));
        var operation = Guid.NewGuid();
        Assert.Equal(preview.Explanation.ReasonCode, Assert.Throws<ExecutionSelectionException>(() =>
            fixture.Service.Start(operation, "capacity-fixture", request, DateTimeOffset.UtcNow)).Message);
        Assert.Null(fixture.Inbox.Take(operation));
        var after = new RecoveryJournalRegistry(source.Database).ReadActiveSnapshot();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Isolations, after.Isolations);
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(OperationRows.ReadOwned(connection, transaction, operation, "capacity-fixture"));
    }

    [Fact(DisplayName = "The History Limit Rejects A Plan Before Its Final Document Or Participant Limits Would Be Exceeded")]
    public void BoundsHistoryDocuments()
    {
        var fixture = ExecutionPlanFixture.Create();
        var plan = ExpandQueue(fixture.Plan, 160);
        var capacity = JournalCapacityPolicy.Measure(plan);
        Assert.True(capacity.MaximumDocumentBytes < StrictJson.MaximumBytes); Assert.True(capacity.Participants < JournalCapacityLimits.MaximumParticipants);
        Assert.True(capacity.Revisions < JournalCapacityLimits.MaximumRevisions); Assert.True(capacity.HistoryBytes > JournalCapacityLimits.MaximumHistoryBytes);
        var error = Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(plan));
        Assert.Equal(JournalCapacityPolicy.Reason, error.Message);
    }

    [Fact(DisplayName = "Wide Step Ids And Deferred Configuration Can Exhaust The History Budget Before Dispatch")]
    public void BoundsDeferredHistory()
    {
        var source = ExecutionPlanFixture.CreateManagedPaths().Plan;
        var configured = ExpandQueue(source, 42);
        var ordinary = configured with { ConfigurationSlots = [] };
        JournalCapacityPolicy.Require(ordinary);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(configured)).Message);
        var wide = ExpandQueue(source with { ConfigurationSlots = [] }, 42, wide: true);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(wide)).Message);
    }

    [Fact(DisplayName = "Frozen Owned Targets Reserve Every Bounded Retention Revision Before Admission")]
    public void ReservesTargetRetention()
    {
        var source = ExecutionPlanFixture.Create().Plan;
        JournalCapacityPolicy.Require(ExpandQueue(source, 42));
        var withTarget = ExpandQueue(WithPcTarget(source), 42);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(withTarget)).Message);
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
        if (accepted)
        {
            var recovered = Assert.Single(TargetJobCodec.Read(parsed.RootElement, plan, binding.TotalPlanSha256));
            Assert.Equal(binding, recovered.Binding);
            Assert.Equal("recoveredRetained", recovered.Stage);
        }
        else Assert.Equal("execution_target_job_unrecognized", Assert.Throws<JsonException>(() =>
            TargetJobCodec.Read(parsed.RootElement, plan, binding.TotalPlanSha256)).Message);
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
        JournalCapacityPolicy.Require(plan with { Steps = [plan.Steps[0] with { Adapter = null }] });
        Assert.True(JournalCapacityPolicy.Measure(plan).MaximumDocumentBytes > StrictJson.MaximumBytes);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(plan)).Message);
    }

    private static ExecutionPlan ExpandQueue(ExecutionPlan source, int count, bool wide = false)
    {
        var step = source.Steps[0];
        var prefix = wide ? new string('界', 115) : "step-";
        var steps = Enumerable.Range(0, count).Select(index => step with { StepId = prefix + index.ToString("D4"), MaximumAttempts = 1 }).ToArray();
        return source with
        {
            Steps = steps,
            ConfigurationSlots = steps.Select(item => item.StepId).SelectMany(id => source.ConfigurationSlots.Select(slot => slot with { StepId = id })).ToArray()
        };
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
