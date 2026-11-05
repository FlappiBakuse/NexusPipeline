using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

internal static class MaaPreparationSource
{
    internal static void Prepare(ProjectSourceFixture project, string package, string marker)
    {
        MaaInitializationSource.Prepare(project, package, marker + ".initialized", false, false);
        using (var archive = ZipFile.OpenRead(package))
            foreach (var entry in archive.Entries.Where(entry => entry.FullName == "runtime/win-x64/" + entry.Name &&
                entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var destination = Path.Combine(project.Path, "native", entry.Name);
                if (File.Exists(destination)) continue;
                using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                input.CopyTo(output);
            }
        var declaration = Path.Combine(project.Path, "assets/interface.json");
        var pi = JsonNode.Parse(File.ReadAllBytes(declaration))!.AsObject();
        pi["pretask"]!["args"] = new JsonArray("startup-child-fixture", Path.Combine(project.Path, "run.exe"), marker);
        File.WriteAllBytes(declaration, JsonSerializer.SerializeToUtf8Bytes(pi));
    }

    internal const string Adapter = """
        globalThis.nexusTaskAdapter = input => {
          const preparation = JSON.parse(nexusReadCompiled('execution', '/Pretasks'));
          const rows = preparation.concat(JSON.parse(nexusReadCompiled('execution', '/Tasks')));
          const result = { format: input.format, version: input.version, direction: 'response', phase: input.phase,
            extensionId: input.extensionId, operationId: input.operationId };
          if (input.phase === 'observe' || input.phase === 'retry') {
            result.runId = input.runId; result.attemptId = input.attemptId; result.planDigest = input.planDigest;
          }
          if (input.phase === 'observe') result.observations = input.tasks.map(task => ({ taskId: task.taskId,
            state: 'unknown', reasonCode: 'controlled_consumer_has_no_business_claim' }));
          else if (input.phase === 'retry') { result.idempotencyKey = input.idempotencyKey; result.selectedTaskIds = []; }
          else { result.tasks = rows.map(row => ({ taskId: 'consumer:' + row.Id, executionTaskId: row.Id, title: row.Name,
            isSelected: row.Enabled, isEnabled: row.Enabled, dependencies: [], retrySafety: 'unknown',
            workflowRole: preparation.some(pre => pre.Id === row.Id) ? 'technical' : 'business' })); result.diagnostics = []; }
          return result;
        };
        """;

    internal static void Verify(ExtensionCatalogFixture catalog, AttemptWriterState state, string marker)
    {
        Assert.True(File.Exists(marker + ".launcher-returned"));
        Assert.False(state.Total.Steps[0].Target!.LaunchByHost);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        var blobs = new ImmutableBlobStore(catalog.Instance, new());
        var evidence = StepEvidenceCodec.Read(blobs, result.Binding, result.Latest.Artifact);
        var raw = RawOutputCodec.Read(blobs, result.Binding, evidence.RawOutput!);
        var events = new List<TaskLogEvent>();
        Assert.True(AdapterLogReader.Read(blobs, raw, (batch, _) => events.AddRange(batch)));
        var line = Assert.Single(events, item => item.Text.StartsWith("{\"format\":\"nexus.maa.program-fact\",", StringComparison.Ordinal));
        using var fact = JsonDocument.Parse(line.Text);
        Assert.Equal("succeeded", fact.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(state.Total.Steps[0].Tasks[0].TaskId, fact.RootElement.GetProperty("taskId").GetString());
    }
}
