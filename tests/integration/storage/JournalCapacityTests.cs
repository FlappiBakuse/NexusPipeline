using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JournalCapacityTests(ITestOutputHelper output)
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
        var plan = CapacityQueue(fixture, 160);
        var document = SampleJournal(plan);
        var participants = document["participants"]!.AsArray();
        using var encoded = new MemoryStream();
        long peak = 0, history = 0, revision = 1;
        RecordRevision();
        var root = ProcessIdentityReader.Read(Environment.ProcessId);
        var raw = fixture.Blobs.PersistPlan("{\"stream\":\"controlled output\"}"u8.ToArray());
        var closure = fixture.Blobs.PersistPlan("{\"closed\":true}"u8.ToArray());
        foreach (var step in plan.Steps)
        {
            var binding = SampleBinding(plan, step.StepId);
            var registered = new StepWriterIntent(WriterIntentCodec.Format, 1, step.StepId, "registered",
                binding.JobName, binding.WriterId, binding.SessionId, null);
            Publish(WriterIntentCodec.Participant(registered, step.ScriptId));
            if (document["phase"]!.GetValue<string>() == "prepared")
            {
                document["phase"] = "applying";
                Publish();
            }
            var evidence = new StepEvidenceDocument(StepEvidenceCodec.Format, 1, binding, "observed", raw, root, 0,
                EngineOutcome.Succeeded, BusinessOutcome.Succeeded, 0, "execution_step_observed", null, null);
            var first = StepEvidenceCodec.Write(fixture.Blobs, evidence);
            var observed = new StepResultIntent(StepResultCodec.Format, 1, binding, "observed", first,
                EngineOutcome.Succeeded, BusinessOutcome.Succeeded, 0, "execution_step_observed");
            Publish(StepResultCodec.Participant(observed, step.ScriptId));
            Publish(WriterIntentCodec.Participant(registered with { Stage = "closed", ElapsedMilliseconds = 1000 }, step.ScriptId));
            var last = StepEvidenceCodec.Write(fixture.Blobs, evidence with { Stage = "collected", PreviousObservation = first, Closure = closure });
            Publish(StepResultCodec.Participant(observed with { Stage = "collected", Artifact = last }, step.ScriptId));
        }
        Assert.InRange(peak, 1, 2_097_152);
        Assert.InRange(participants.Count, 1, 4_096);
        Assert.InRange(revision, 1, 4_096);
        Assert.True(history > 67_108_864, $"Encoded execution history is {history} bytes");
        var capacity = JournalCapacityPolicy.Measure(plan);
        Assert.True(capacity.MaximumDocumentBytes >= peak && capacity.HistoryBytes >= history);
        Assert.True(capacity.Participants >= participants.Count && capacity.Revisions >= revision);
        output.WriteLine($"steps={plan.Steps.Count}; revisions={revision}; peakDocumentBytes={peak}; fullHistoryBytes={history}");
        var error = Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(plan));
        Assert.Equal(JournalCapacityPolicy.Reason, error.Message);

        void Publish(JsonObject? participant = null)
        {
            document["revision"] = ++revision;
            if (participant is not null) participants.Add(participant);
            RecordRevision();
        }

        void RecordRevision()
        {
            encoded.SetLength(0);
            JsonSerializer.Serialize(encoded, document);
            peak = Math.Max(peak, encoded.Length);
            history += encoded.Length;
        }
    }

    private static ExecutionPlan CapacityQueue(ExecutionPlanFixture fixture, int count)
    {
        var original = fixture.Plan;
        var template = Assert.Single(original.Steps);
        var bundle = fixture.Bundle.DeepClone().AsObject();
        bundle["scripts"]![0]!["limits"]!["maximumAttempts"] = 1;
        var queueId = Guid.NewGuid().ToString("D");
        bundle["queues"] = JsonSerializer.SerializeToNode(new[] { new AutomationRelationsQueue
        {
            Id = queueId, Name = "Capacity queue", Order = 0, ScriptIds = [template.ScriptId], CompletionAction = "none",
            NotifyEnabled = false, NotificationTaskIds = []
        } });
        var accounts = Enumerable.Range(0, count).Select(index => new
        {
            Order = index,
            UserId = Guid.NewGuid().ToString("D"),
            BindingId = Guid.NewGuid().ToString("D")
        }).ToArray();
        bundle["users"] = JsonSerializer.SerializeToNode(accounts.Select(item => new
            { id = item.UserId, name = "Capacity " + item.Order, order = item.Order }));
        bundle["bindings"] = JsonSerializer.SerializeToNode(accounts.Select(item => new
            { id = item.BindingId, userId = item.UserId, scriptId = template.ScriptId, enabled = true, priority = 1,
                notifyEnabled = false, notificationTaskIds = Array.Empty<string>() }));
        var selection = accounts.Select(item => new ExecutionPlanSelectionItem
        {
            StepId = $"binding-{item.BindingId}-{item.Order:D4}", BindingId = item.BindingId,
            UserId = item.UserId, ScriptId = template.ScriptId, Disposition = "runnable", ReasonCode = "execution_inputs_verified"
        }).ToArray();
        var plan = fixture.WithBundle(bundle) with
        {
            QueueId = queueId,
            Selection = selection,
            SourceRevisions = original.SourceRevisions.Where(item => item.EntityKind is "root" or "script")
                .Concat(accounts.SelectMany(item => new[] { new ExecutionPlanSourceRevisionsItem { EntityKind = "user", EntityId = item.UserId, Revision = 1 },
                    new ExecutionPlanSourceRevisionsItem { EntityKind = "binding", EntityId = item.BindingId, Revision = 1 } }))
                .Append(new() { EntityKind = "queue", EntityId = queueId, Revision = 1 }).ToArray(),
            Steps = selection.Select(item => template with { StepId = item.StepId, BindingId = item.BindingId, UserId = item.UserId, MaximumAttempts = 1 }).ToArray(),
            Budget = original.Budget with { TotalTimeoutSeconds = 120 * count }
        };
        plan = plan with { RetryPolicy = ExecutionRetryAuthority.Capture(plan, new Dictionary<string, int> { [template.ScriptId] = 1 }, new(true, 0)) };
        plan = plan with { ResourceKeys = ExecutionPlanResources.Derive(plan) };
        var wire = new ContractReader().Read<AutomationBundle>(JsonSerializer.SerializeToUtf8Bytes(bundle), "automation-bundle.schema.json");
        ExecutionPlanSources.Validate(plan, wire);
        return plan;
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
        string[] sampleIds = ["controlled:failed", "controlled:complete"];
        var sampleStep = plan.Steps[0] with
        {
            Tasks = [step.Tasks[0] with { TaskId = sampleIds[0], NativeResumeScope = sampleIds },
                step.Tasks[0] with { TaskId = sampleIds[1] }],
            SelectedTaskIds = sampleIds
        };
        var samplePlan = plan with { Steps = [sampleStep] };
        var document = SampleJournal(plan);
        var participants = document["participants"]!.AsArray();
        var ids = ImmutableArray.CreateRange(tasks.Select(task => task.TaskId));
        var prior = SampleBinding(plan, step.StepId);
        var writer = new StepWriterIntent(WriterIntentCodec.Format, 1, step.StepId, "registered",
            prior.JobName, prior.WriterId, prior.SessionId, null);
        participants.Add(WriterIntentCodec.Participant(writer, step.ScriptId));
        document["phase"] = "applying";
        const long priorRevision = 6;
        document["revision"] = priorRevision;
        var blobs = fixture.Blobs;
        var stdout = blobs.PersistContent("controlled failure\n"u8.ToArray(), default);
        var raw = RawOutputCodec.Write(blobs, new(RawOutputCodec.Format, 1, prior, Guid.NewGuid(), true,
            stdout.SizeBytes, 0, stdout.SizeBytes, stdout.SizeBytes + 1, stdout.SizeBytes, stdout.SizeBytes + 2, null,
            [new(0, ProcessOutputSource.StandardOutput, stdout)]));
        var observation = AdapterObservationCodec.Write(blobs, prior, raw, sampleStep,
            [new(sampleIds[0], AdapterObservationState.Failed, "fixture_task_failed"),
             new(sampleIds[1], AdapterObservationState.Succeeded, "fixture_task_completed", AdapterCompletion.Completed)],
            [sampleIds[0]]);
        var closure = blobs.PersistPlan("{\"closed\":true}"u8.ToArray());
        var root = ProcessIdentityReader.Read(Environment.ProcessId);
        const string reason = "execution_engine_failed";
        var evidence = new StepEvidenceDocument(StepEvidenceCodec.Format, 1, prior, "observed", raw, root, 7,
            EngineOutcome.Failed, BusinessOutcome.Failed, 0, reason, null, null);
        var firstArtifact = StepEvidenceCodec.Write(blobs, evidence);
        var observed = new StepResultIntent(StepResultCodec.Format, 1, prior, "observed", firstArtifact,
            EngineOutcome.Failed, BusinessOutcome.Failed, 0, reason);
        participants.Add(StepResultCodec.Participant(observed, step.ScriptId));
        participants.Add(WriterIntentCodec.Participant(writer with { Stage = "closed", ElapsedMilliseconds = 1000 }, step.ScriptId));
        var collectedArtifact = StepEvidenceCodec.Write(blobs, evidence with
        {
            Stage = "collected", PreviousObservation = firstArtifact, Closure = closure, TaskAdapter = observation
        });
        participants.Add(StepResultCodec.Participant(observed with { Stage = "collected", Artifact = collectedArtifact }, step.ScriptId));
        using var before = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(document));
        var previous = Assert.Single(StepResultCodec.Read(before.RootElement, samplePlan));
        Assert.True(previous.IsCollected);
        var saved = StepEvidenceCodec.Read(blobs, previous.Binding, previous.Latest.Artifact);
        Assert.Equal(raw, saved.RawOutput);
        Assert.Equal(observation, saved.TaskAdapter);
        Assert.True(RawOutputCodec.Read(blobs, previous.Binding, saved.RawOutput!).IsComplete);
        var previousTasks = AdapterObservationCodec.Read(blobs, previous.Binding, saved.RawOutput!, sampleStep, saved.TaskAdapter!);
        var selected = AdapterRetryPolicy.Select(TaskRetryEvidence.Tasks(sampleStep), previousTasks.Observations, previousTasks.ProposedRetry);
        Assert.Equal(new[] { sampleIds[0] }, selected.Targets);
        Assert.Equal(sampleIds, selected.SelectedIds);
        var retry = new ExecutionInvocation(ExecutionInvocationCodec.Format, 1, step.StepId, 2,
            before.RootElement.GetProperty("revision").GetInt64(), previous.Binding.JobName,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), [])
        {
            TaskRetry = new(previous.Binding, saved.RawOutput!, saved.TaskAdapter!, selected)
        };
        document["revision"] = priorRevision + 1;
        participants.Add(ExecutionInvocationCodec.Participant(retry, step.ScriptId));
        var narrowBytes = JsonSerializer.SerializeToUtf8Bytes(document);
        Assert.InRange(narrowBytes.Length, 1, 2_097_152);
        using (var encoded = JsonDocument.Parse(narrowBytes))
        {
            var decoded = Assert.Single(ExecutionInvocationCodec.Read(encoded.RootElement, samplePlan));
            Assert.True(TaskRetryEvidence.Same(retry.TaskRetry, decoded.TaskRetry));
        }
        JournalCapacityPolicy.Require(samplePlan);
        // Substitute the largest permitted selection vectors to exercise the serializer's document limit.
        var wideRetry = retry with { TaskRetry = retry.TaskRetry! with { Selection = new(ids, ids) } };
        participants[^1] = ExecutionInvocationCodec.Participant(wideRetry, step.ScriptId);
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(document).LongLength;
        output.WriteLine($"narrowRetryBytes={narrowBytes.Length}; wideRetryBytes={actualBytes}; idsPerArray={ids.Length}");
        Assert.True(actualBytes > 2_097_152, $"Encoded retry document is {actualBytes} bytes");
        Assert.True(JournalCapacityPolicy.Measure(plan).MaximumDocumentBytes >= actualBytes);
        Assert.Equal(JournalCapacityPolicy.Reason, Assert.Throws<ExecutionSelectionException>(() => JournalCapacityPolicy.Require(plan)).Message);
    }

    private static readonly Guid SampleJournalId = Guid.Parse("31111111-1111-1111-1111-111111111111");
    private static readonly Guid SampleOperationId = Guid.Parse("32222222-2222-2222-2222-222222222222");
    private static readonly Guid SampleAttemptId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static BlobReference SampleBlob(char digit) => new($"blobs/plans/{new string(digit, 64)}.json", 31, new string(digit, 64));

    private static JsonObject SampleJournal(ExecutionPlan plan)
    {
        var reference = SampleBlob('f');
        var journal = new RegisteredJournal(SampleJournalId, SampleOperationId, JournalKind.RunAttempt, JournalPhase.Prepared,
            1, reference.RelativePath, reference.Sha256, plan.ResourceKeys);
        return JsonSerializer.SerializeToNode(ExecutionInitialJournal.Create(Guid.Parse(plan.InstanceId), journal, plan.RunId, SampleAttemptId.ToString("D")))!.AsObject();
    }

    private static StepEvidenceBinding SampleBinding(ExecutionPlan plan, string step) =>
        new(Guid.Parse(plan.InstanceId), SampleJournalId, SampleOperationId, Guid.Parse(plan.RunId),
            SampleAttemptId, step, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 1, new('f', 64));

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
