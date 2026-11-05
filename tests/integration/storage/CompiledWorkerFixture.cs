using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class CompiledWorkerFixture
{
    internal static void Declare(JsonObject manifest, string marker, string mode)
    {
        manifest["capabilities"]!.AsArray().Add("execution.drive");
        manifest["permissions"]!["processAccess"] = new JsonArray("owned-worker", "selected-upstream");
        manifest["execution"] = new JsonObject
        {
            ["entryId"] = "main", ["workerEntry"] = "worker/" + Path.GetFileName(Worker()),
            ["arguments"] = new JsonArray("provider-fixture-worker", mode, marker),
            ["protocol"] = "nexus.execution.provider/1", ["configurationResources"] = new JsonArray()
        };
    }

    internal static async Task RunAsync(ExtensionCatalogFixture catalog, ExecutionAdmissionInbox inbox, TaskAdapterService adapters,
        ProjectSourceFixture project, Guid journalId, string mode)
    {
        var originalConfiguration = File.ReadAllBytes(project.ConfigurationFile!);
        var runtime = new ExecutionRuntimeConsumer(catalog.Instance, catalog.Database, new(), catalog.Lease, catalog.Resources, catalog.Index, inbox);
        runtime.ConfigureTaskAdapters(adapters);
        var running = runtime.RunNextAsync();
        if (mode == "project-secret-revision")
        {
            var marker = Path.Combine(project.Path, "synthetic-worker-ready");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker + ".secret-ready")) await Task.Delay(10, timeout.Token);
            _ = await new SecretFieldStore(catalog.Instance, catalog.Database).ApplyAsync(Guid.NewGuid(),
                new("fixture-plugin", project.Binding, "input-fixture"), 1, SecretDisposition.Clear, null, default);
            File.WriteAllText(marker + ".secret-release", "released");
        }
        var receipt = Assert.IsType<AttemptReceipt>(await running);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        var state = catalog.Lease.PublishExecution(() => AttemptWriterState.Read(catalog.Instance, catalog.Database, new(), journalId));
        if (project.HasTaskRetry)
        {
            CompiledRetryChecks.Verify(catalog, project, state, receipt, originalConfiguration);
            return;
        }
        var actual = Assert.Single(state.Total.Steps); Assert.Equal("maas", actual.ExtensionId); Assert.Equal("fixture-plugin", actual.Provider!.ExtensionId);
        var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        var transcript = new ProviderControlReader(catalog.Instance, catalog.Database, new()).Read(terminal.Binding, terminal.Artifact);
        using var read = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(project.Path, "synthetic-worker-ready.project")));
        Assert.Equal(actual.Adapter!.Compiled!.Sha256, read.RootElement.GetProperty("Fingerprint").GetString());
        var observedPid = read.RootElement.GetProperty("processId").GetInt32();
        if (mode == "project-read")
        {
            Assert.Equal(3 * 1024 * 1024, read.RootElement.GetProperty("SizeBytes").GetInt32());
            var selection = read.RootElement.GetProperty("selection");
            Assert.Equal(actual.ExtensionId, selection.GetProperty("consumerExtensionId").GetString());
            Assert.Equal(actual.BindingId, selection.GetProperty("bindingId").GetString());
            Assert.Equal(actual.SelectedTaskIds, selection.GetProperty("selectedTaskIds").EnumerateArray().Select(item => item.GetString()!));
            var workspace = read.RootElement.GetProperty("workspace");
            var path = workspace.GetProperty("path").GetString()!;
            Assert.StartsWith(Path.Combine(catalog.Instance.RuntimeRoot, "work") + Path.DirectorySeparatorChar, path);
            var identity = NexusPipeline.Platform.Storage.PhysicalObjectIdentity.Read(path);
            Assert.Equal(identity.VolumeSerial, workspace.GetProperty("identity").GetProperty("volumeSerial").GetString());
            Assert.Equal(identity.FileId, workspace.GetProperty("identity").GetProperty("fileId").GetString());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(path, "inputs/image.png")));
            Assert.Equal("native-fixture-output", File.ReadAllText(Path.Combine(path, "outputs/result.txt")));
            Assert.Equal(new[] { "inputs", "outputs" }, Directory.GetDirectories(path).Select(Path.GetFileName).Order().ToArray());
            var stepResult = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
            var blobs = new NexusPipeline.Platform.Storage.ImmutableBlobStore(catalog.Instance, new());
            var evidence = StepEvidenceCodec.Read(blobs, stepResult.Binding, stepResult.Latest.Artifact);
            Assert.NotNull(evidence.WorkspaceOutput);
            var references = new StepEvidenceReader(catalog.Instance, catalog.Database, new()).ReadReferences(stepResult.Binding, stepResult.Latest.Artifact);
            Assert.Contains(evidence.WorkspaceOutput, references);
            ProviderOutputArchive archive;
            using (var input = blobs.OpenVerified(evidence.WorkspaceOutput))
                archive = JsonSerializer.Deserialize<ProviderOutputArchive>(input, StepResultCodec.Options)!;
            var forged = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(archive with { Closure = evidence.ProviderControl! }, StepResultCodec.Options));
            Assert.Throws<IOException>(() => ProviderOutputCodec.Read(catalog.Instance, catalog.Database, new(), stepResult.Binding,
                forged, evidence.ProviderControl!, evidence.Closure!, true));
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("native-fixture-output"u8));
            var content = Assert.Single(references.Distinct(), item => item.Sha256 == sha);
            var recovered = ProviderOutputCodec.Recover(catalog.Instance, catalog.Database, new(), stepResult.Binding, evidence.ProviderControl!);
            Assert.NotNull(recovered);
            Assert.Contains(content, ProviderOutputCodec.Read(catalog.Instance, catalog.Database, new(), stepResult.Binding,
                recovered, evidence.ProviderControl!, evidence.Closure!, true));
            Directory.Move(Path.Combine(path, "outputs"), Path.Combine(path, "retired-live"));
            Assert.Throws<IOException>(() => ProviderOutputCodec.Recover(catalog.Instance, catalog.Database, new(),
                stepResult.Binding, evidence.ProviderControl!));
            Assert.Equal(stepResult.Latest.Artifact, new StepEvidenceReader(catalog.Instance, catalog.Database, new())
                .Read(stepResult.Binding, stepResult.Latest.Artifact, true).Artifact);
            using var archived = blobs.OpenVerified(content); using var text = new StreamReader(archived);
            Assert.Equal("native-fixture-output", text.ReadToEnd());
        }
        else if (mode.StartsWith("project-secret", StringComparison.Ordinal))
        {
            Assert.Equal(mode != "project-secret-revision", read.RootElement.GetProperty("verified").GetBoolean());
            Assert.Equal(mode != "project-secret-revision", read.RootElement.GetProperty("disposed").GetBoolean());
            Assert.Equal(mode != "project-secret", read.RootElement.GetProperty("denied").GetBoolean());
            Assert.DoesNotContain("PROTECTED_PIPE_FIXTURE", read.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("PROTECTED_PIPE_FIXTURE", JsonSerializer.Serialize(transcript), StringComparison.Ordinal);
        }
        else Assert.True(read.RootElement.GetProperty("denied").GetBoolean());
        if (mode == "project-peer") Assert.NotEqual(transcript.Archive.Peer.ProcessId, observedPid);
        else Assert.Equal(transcript.Archive.Peer.ProcessId, observedPid);
        var closure = new JobClosureStore(catalog.Instance, catalog.Database, new()).ReadForExecutionEvidence(
            terminal.Binding.ParentJournalId, terminal.Binding.WriterId, terminal.Binding.JobName, terminal.Binding.SessionId);
        Assert.NotNull(closure); Assert.True(closure.WasExecuted);
        Assert.Contains(closure.Family, member => member.ProcessId == observedPid);
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        Assert.Equal(originalConfiguration, File.ReadAllBytes(project.ConfigurationFile!));
    }

    internal static void Populate(string directory)
    {
        var worker = Worker(); var parent = Path.GetDirectoryName(worker)!;
        var runtime = JudgementInterpreterLease.Inspect(worker);
        foreach (var relative in runtime.RuntimeFiles.Select(item => item.RelativePath).Append(Path.GetFileName(worker)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(directory, "worker", relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(parent, relative), target);
        }
    }

    private static string Worker() => Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")
        ?? throw new IOException("NEXUS_PROCESS_FIXTURE_EXE_required");
}
