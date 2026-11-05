using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

internal sealed record ExecutionPlanFixture(string Root, string Image, RuntimeInstance Instance,
    ImmutableBlobStore Blobs, ExecutionPlan Plan, ExecutionPlanCodec Codec, JsonObject Bundle, StateDatabase Database)
{
    internal static ExecutionPlanFixture Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "execution-plan-" + Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var scriptRoot = Path.Combine(root, "script");
        Directory.CreateDirectory(scriptRoot);
        var image = Path.Combine(scriptRoot, "owned.bin");
        File.WriteAllBytes(image, [0, 1, 255]);
        var rootId = Guid.NewGuid().ToString("D");
        var scriptId = Guid.NewGuid().ToString("D");
        var userId = Guid.NewGuid().ToString("D");
        var bindingId = Guid.NewGuid().ToString("D");
        var bundle = JsonSerializer.SerializeToNode(new
        {
            format = "nexus.automation.bundle", version = 1, bundleId = Guid.NewGuid().ToString("D"),
            createdAt = "2026-10-19T00:00:00Z", exportKind = "definitions",
            roots = new[] { new { id = rootId, name = "Fixture", kind = "script" } },
            scripts = new[] { new
            {
                id = scriptId, name = "Owned fixture", kind = "generic", rootId, updateConfigurationAtFinish = true,
                executable = new { rootId, relative = "owned.bin" }, arguments = new[] { "--fixture" },
                configurationPaths = Array.Empty<object>(), logPaths = Array.Empty<object>(), adapterResources = Array.Empty<object>(),
                limits = new { maximumAttempts = 2, stallTimeoutSeconds = 30, totalTimeoutSeconds = 120 },
                judgement = new { successKeywords = Array.Empty<string>(), failureKeywords = Array.Empty<string>(), language = "keywords" }
            } },
            users = new[] { new { id = userId, name = "Test", order = 0 } },
            bindings = new[] { new { id = bindingId, userId, scriptId, enabled = true, priority = 1,
                notifyEnabled = false, notificationTaskIds = Array.Empty<string>() } },
            queues = Array.Empty<object>(), schedules = Array.Empty<object>(), attachments = Array.Empty<object>()
        })!.AsObject();
        var blobs = new ImmutableBlobStore(instance, contracts);
        var content = PersistBundle(root, bundle, blobs);
        var identity = PhysicalObjectIdentity.Read(scriptRoot);
        FileTreePhysicalIdentity Wire(PhysicalObjectIdentity value) => new() { VolumeSerial = value.VolumeSerial, FileId = value.FileId };
        var runId = Guid.NewGuid().ToString("D");
        var plan = new ExecutionPlan
        {
            Format = "nexus.execution.plan", Version = 1, InstanceId = instance.InstanceId.ToString("D"), RunId = runId,
            CreatedAt = "2026-10-19T00:00:00Z", DefinitionBundle = content, SettingsRevision = 1,
            Selection = [new() { StepId = "first", BindingId = bindingId, ScriptId = scriptId, UserId = userId,
                Disposition = "runnable", ReasonCode = "execution_inputs_verified" }],
            SourceRevisions = new[] { ("root", rootId), ("script", scriptId), ("user", userId), ("binding", bindingId) }
                .Select(item => new ExecutionPlanSourceRevisionsItem { EntityKind = item.Item1, EntityId = item.Item2, Revision = 1 }).ToArray(),
            RootBindings = [new() { RootId = rootId, Path = scriptRoot, Identity = Wire(identity) }],
            Steps = [new()
            {
                StepId = "first", ScriptId = scriptId, UserId = userId, BindingId = bindingId, EngineKind = "process", DependsOn = [],
                ExecutablePath = image, ImageSizeBytes = 3, ImageSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image))),
                ImageIdentity = Wire(PhysicalObjectIdentity.Read(image)), ParentIdentity = Wire(identity), WorkingDirectory = scriptRoot,
                WorkingDirectoryIdentity = Wire(identity), Arguments = ["--fixture"],
                Tasks = [new() { TaskId = "one", Title = "First", IsEnabled = true, IsSelected = true, Dependencies = [], RetrySafety = "safe" }],
                SelectedTaskIds = ["one"], ConfigurationTargets = [], LogTargets = [], UpdateConfigurationAtFinish = true,
                MaximumAttempts = 2, TotalTimeoutSeconds = 120
            }],
            ConfigurationSlots = [], PhysicalClaims = [], ResourceKeys = [], CompletionAction = "none", ExtensionLocks = [],
            Budget = new() { TotalTimeoutSeconds = 120, StopTimeoutSeconds = 5, DrainTimeoutSeconds = 5, JudgementTimeoutSeconds = 5 },
            RetryPolicy = new() { Safety = "unknown", HostApproved = true, MaximumAttempts = 1,
                DeadlineUtc = "2026-10-19T00:02:00.0000000Z", BackoffSeconds = 0, IdempotencyKey = runId }
        };
        plan = plan with { RetryPolicy = ExecutionRetryAuthority.Capture(plan,
            new Dictionary<string, int> { [scriptId] = 2 }, new(true, 0)) };
        plan = plan with { PhysicalClaims = ExecutionPlanResources.Capture(plan) };
        plan = plan with { ResourceKeys = ExecutionPlanResources.Derive(plan) };
        return new(root, image, instance, blobs, plan, new(instance, contracts, blobs), bundle, database);
    }

    internal ExecutionPlan WithBundle(JsonObject bundle) => Plan with { DefinitionBundle = PersistBundle(Root, bundle, Blobs) };

    internal static ExecutionPlanFixture CreateManagedPaths()
    {
        var fixture = Create();
        var scriptRoot = fixture.Plan.RootBindings[0].Path;
        var config = Path.Combine(scriptRoot, "configuration");
        Directory.CreateDirectory(Path.Combine(config, "empty"));
        File.WriteAllBytes(Path.Combine(config, "settings.bin"), [3, 4, 5]);
        var log = Path.Combine(scriptRoot, "events.log");
        File.WriteAllBytes(log, [6, 7, 8]);
        var bundle = fixture.Bundle.DeepClone().AsObject();
        var rootId = fixture.Plan.RootBindings[0].RootId;
        bundle["scripts"]![0]!["configurationPaths"] = JsonSerializer.SerializeToNode(new[] { new { rootId, relative = "configuration" } });
        bundle["scripts"]![0]!["logPaths"] = JsonSerializer.SerializeToNode(new[] { new { rootId, relative = "events.log" } });
        var plan = fixture.WithBundle(bundle);
        plan = plan with { Steps = [plan.Steps[0] with { ConfigurationTargets = [config],
            LogTargets = [new() { DeclarationIndex = 0, ScopePath = log, Selector = "file" }] }] };
        plan = plan with { ConfigurationSlots = [new() { StepId = plan.Steps[0].StepId, BindingId = plan.Steps[0].BindingId!,
            ResourceId = "main", TargetPath = config, ExpectedRevision = 1 }] };
        plan = plan with { PhysicalClaims = ExecutionPlanResources.Capture(plan) };
        plan = plan with { ResourceKeys = ExecutionPlanResources.Derive(plan) };
        return fixture with { Plan = plan, Bundle = bundle };
    }

    private static ExecutionPlanContentReference PersistBundle(string root, JsonObject bundle, ImmutableBlobStore blobs)
    {
        var path = Path.Combine(root, "definition-input.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(bundle));
        var snapshot = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
        var reference = blobs.PersistContent(path, snapshot.Entries.Single(), default);
        return new() { RelativePath = reference.RelativePath, SizeBytes = reference.SizeBytes, Sha256 = reference.Sha256 };
    }
}
