using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class CompiledTaskChecks
{
    [SupportedOSPlatform("windows")]
    internal static void VerifyOwnedDefinitions(ExtensionCatalogFixture catalog, AcceptedExecutionAdmission accepted, ExecutionPlanStep step)
    {
        var total = accepted.Prepared.Total.Plan.ReadWire();
        var prepared = accepted.Prepared.Configurations[step.StepId];
        var journal = accepted.Receipt.JournalId;
        var store = new ConfigurationRunStore(catalog.Instance, catalog.Database, new());
        var identity = ConfigurationRunStore.BeginIdentity(Guid.NewGuid(), "fixture", journal, step.StepId, prepared);
        var session = store.Begin(identity, journal, step.StepId, prepared, DateTimeOffset.UtcNow);
        using var owner = catalog.Lease.BindAttemptReads(catalog.Database).Start(catalog.Instance, catalog.Database, new(), journal)!;
        Assert.NotNull(owner.ReadDefinitions());
        var expected = ConfigurationRunBinding.Read(catalog.Instance, catalog.Database, new(), session);
        var state = Assert.Single(expected.Resources).State;
        var rules = state.Tasks.Select(item => item.Rule).ToImmutableArray();
        var blobs = new ImmutableBlobStore(catalog.Instance, new());
        var foreignInstance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!,
            "foreign-definitions-" + Guid.NewGuid().ToString("N")));
        var foreign = new ImmutableBlobStore(foreignInstance, new());
        var definition = total.DefinitionBundle;
        using var blocked = new FileStream(Path.Combine(catalog.Instance.RuntimeRoot, definition.RelativePath),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        blocked.Lock(0, blocked.Length);
        try
        {
            Assert.Throws<IOException>(() => ExecutionAdapterBinding.RequireFrozenConfiguration(total, step, state.Source, blobs, new()));
            Assert.Throws<IOException>(() => ExecutionTaskProjection.RequireBoundRules(total, step, state.Source, rules, blobs, new()));
            var actual = ConfigurationRunBinding.Read(catalog.Instance, catalog.Database, new(), session);
            Assert.Equal(expected.Evidence.AttemptId, actual.Evidence.AttemptId);
            Assert.Equal(state.SnapshotReference, Assert.Single(actual.Resources).State.SnapshotReference);
            using (JournalReadScope.HoldAttempt(catalog.Instance, new(), catalog.Database, journal))
            {
                ExecutionAdapterBinding.RequireFrozenConfiguration(total, step, state.Source, blobs, new());
                ExecutionTaskProjection.RequireBoundRules(total, step, state.Source, rules, blobs, new());
                foreach (var changed in new[]
                {
                    (Plan: total with { DefinitionBundle = definition with { RelativePath = definition.RelativePath + ".changed" } }, Error: typeof(ArgumentException)),
                    (Plan: total with { DefinitionBundle = definition with { SizeBytes = definition.SizeBytes + 1 } }, Error: typeof(IOException)),
                    (Plan: total with { DefinitionBundle = definition with { Sha256 = new('b', 64) } }, Error: typeof(ArgumentException))
                })
                {
                    Assert.Throws(changed.Error, () => ExecutionAdapterBinding.RequireFrozenConfiguration(changed.Plan, step, state.Source, blobs, new()));
                    Assert.Throws(changed.Error, () => ExecutionTaskProjection.RequireBoundRules(changed.Plan, step, state.Source, rules, blobs, new()));
                }
                Assert.Null(JournalReadScope.ReadDefinitions(foreign.Instance, total));
                Assert.ThrowsAny<IOException>(() => ExecutionAdapterBinding.RequireFrozenConfiguration(total, step, state.Source, foreign, new()));
                Assert.ThrowsAny<IOException>(() => ExecutionTaskProjection.RequireBoundRules(total, step, state.Source, rules, foreign, new()));
                var captured = Assert.Single(state.Tasks);
                Assert.Throws<IOException>(() => ExecutionTaskProjection.RequireBoundRules(total, step, state.Source,
                    [captured.Rule with { Access = captured.Rule.Access with { Protected = [new([new ConfigurationProperty("taskB")])] } }], blobs, new()));
            }
            owner.Dispose();
            Assert.Throws<IOException>(() => ConfigurationRunBinding.Read(catalog.Instance, catalog.Database, new(), session));
        }
        finally { blocked.Unlock(0, blocked.Length); }
    }

    internal static string Tasks(ProjectSourceFixture project)
    {
        if (!project.HasTaskProjection) return "[]";
        var tasks = new[] { "taskA", "taskB" }.Select(id => new
        {
            taskId = "fixture:" + id, title = id, isSelected = project.HasTaskRetry || id == "taskA", isEnabled = true,
            dependencies = Array.Empty<string>(), retrySafety = "safe",
            projection = new { resourceId = "account", relativePath = "local.json", selector = new[] { id } }
        });
        return "(input.resources?.length ? " + JsonSerializer.Serialize(tasks) + " : [])";
    }

    internal static string Adapter(ProjectSourceFixture project)
    {
        var tasks = Tasks(project);
        return "globalThis.nexusTaskAdapter = input => { const value = JSON.parse(nexusReadCompiled('artifact', '/value')); " +
            "if (value.token !== 42) throw new Error('fixture_unbound_value'); " +
            "const result = { format: input.format, version: input.version, direction: 'response', phase: input.phase, " +
            "extensionId: input.extensionId, operationId: input.operationId }; " +
            "if (input.phase === 'observe' || input.phase === 'retry') { result.runId = input.runId; result.attemptId = input.attemptId; result.planDigest = input.planDigest; } " +
            "if (input.phase === 'observe') { result.observations = input.tasks.filter(t => t.isSelected).map(t => { " +
            "const line = (input.logEvents ?? []).find(e => e.text.startsWith(t.taskId + ':')); " +
            "return { taskId: t.taskId, state: line?.text.slice(t.taskId.length + 1) ?? 'unknown', reasonCode: 'fixture_task_observed', " +
            "...(t.taskId === 'fixture:taskA' && line?.text.endsWith(':succeeded') ? { completionKind: 'satisfied' } : {}) }; }); } " +
            "else if (input.phase === 'retry') { result.idempotencyKey = input.idempotencyKey; result.selectedTaskIds = input.observations.filter(t => t.state === 'failed').map(t => t.taskId); } " +
            "else { result.tasks = " + tasks + "; result.diagnostics = []; } return result; };";
    }

    internal static void Verify(ExtensionCatalogFixture catalog, AcceptedExecutionAdmission accepted, ProjectSourceFixture project, ExecutionPlanStep step)
    {
        var liveBytes = File.ReadAllBytes(project.ConfigurationFile!);
        var prepared = accepted.Prepared.Configurations[step.StepId];
        var rule = Assert.Single(Assert.Single(prepared.Request.Resources).Tasks);
        Assert.IsType<ConfigurationSelectorTasks>(rule.Declaration.Source);
        Assert.Equal(new[] { "fixture:taskA", "fixture:taskB" }, rule.SelectedIds.ToArray());
        prepared.Use(resources =>
        {
            var exchange = Assert.Single(resources).Plan;
            var blobs = new ImmutableBlobStore(catalog.Instance, new());
            var state = new RunStateCodec(catalog.Instance, new(), blobs).Read(exchange.RunState!);
            var captured = Assert.Single(state.Tasks);
            var altered = (ConfigurationSelectorTasks)captured.Rule.Declaration.Source;
            altered = altered with { Targets = altered.Targets.SetItem(0, altered.Targets[0] with
                { Selector = new([new ConfigurationProperty("unlisted")]) }) };
            Assert.Throws<IOException>(() => ExecutionTaskProjection.RequireRules(step, "account",
                [captured.Rule with { Declaration = captured.Rule.Declaration with { Source = altered } }]));
            Assert.Throws<IOException>(() => ExecutionTaskProjection.RequireValues(step,
                [captured with { Initial = captured.Initial with { Values = captured.Initial.Values.SetItem("fixture:taskB", false) } }]));
            Assert.Throws<IOException>(() => ExecutionTaskProjection.RequireBoundRules(accepted.Prepared.Total.Plan.ReadWire(), step,
                state.Source, [captured.Rule with { Access = captured.Rule.Access with { Protected = [new([new ConfigurationProperty("taskB")])] } }], blobs, new()));
            Assert.True(captured.Initial.Values["fixture:taskA"]);
            Assert.True(captured.Initial.Values["fixture:taskB"]);
            var entry = exchange.Candidate.Snapshot.Entries.Single(item => item.RelativePath == "local.json");
            using var input = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            using var candidate = JsonDocument.Parse(input);
            Assert.Equal("saved-account", candidate.RootElement.GetProperty("account").GetString());
            Assert.True(candidate.RootElement.GetProperty("taskA").GetBoolean());
            Assert.True(candidate.RootElement.GetProperty("taskB").GetBoolean());
            Assert.True(candidate.RootElement.GetProperty("unlisted").GetBoolean());
            Assert.Equal(17, candidate.RootElement.GetProperty("counter").GetInt32());
            var subset = state.Tasks.SetItem(0, captured with { Rule = captured.Rule with { SelectedIds = ["fixture:taskA"] } });
            var projected = new ConfigurationTaskTransformation(blobs).Select(state.ConfigurationOperationId, state.Source, subset);
            var projectedEntry = projected.Entries.Single(item => item.RelativePath == "local.json");
            using var projectedInput = blobs.OpenVerified(new($"blobs/content/{projectedEntry.Sha256}.bin", projectedEntry.SizeBytes, projectedEntry.Sha256!));
            using var projectedValue = JsonDocument.Parse(projectedInput);
            Assert.False(projectedValue.RootElement.GetProperty("taskB").GetBoolean());
            Assert.True(projectedValue.RootElement.GetProperty("unlisted").GetBoolean());
            Assert.Equal(17, projectedValue.RootElement.GetProperty("counter").GetInt32());
            var restored = new ConfigurationTaskTransformation(blobs).Restore(state.ConfigurationOperationId, state.Source,
                subset, projected);
            Assert.Equal(state.Source.Tree.Entries, restored.Entries);
            return true;
        });
        Assert.Equal(liveBytes, File.ReadAllBytes(project.ConfigurationFile!));
    }
}
