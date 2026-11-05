using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ProviderUseTests
{
    [Theory(DisplayName = "An Execution Provider Dependency Is An Explicit External Manifest Entry Independent Of The Compiler")]
    [InlineData("valid")]
    [InlineData("self")]
    [InlineData("missing-entry")]
    [InlineData("extra")]
    [InlineData("invalid-id")]
    [InlineData("wrong-kind")]
    public void RequiresExplicitProvider(string mode)
    {
        var manifest = Manifest(); var use = manifest["specialized"]!["executionProvider"]!.AsObject();
        if (mode == "self") use["extensionId"] = "consumer";
        if (mode == "missing-entry") use.Remove("entryId");
        if (mode == "extra") use["permission"] = "execute";
        if (mode == "invalid-id") use["extensionId"] = "../outside";
        if (mode == "wrong-kind") manifest["kind"] = "managed-code";
        var reader = new ExtensionManifestAdmission(new());
        if (mode != "valid") { Assert.Throws<JsonException>(() => reader.ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), "consumer")); return; }
        var result = reader.ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(manifest), "consumer");
        Assert.Equal("driver", result.Specialized!.ExecutionProvider!.ExtensionId); Assert.Equal("main", result.Specialized.ExecutionProvider.EntryId);
        Assert.Equal("compiler", result.Specialized.Compiler!.ExtensionId);
    }

    [Theory(DisplayName = "Frozen Adapter Consumers And Shared Worker Providers Retain Separate Explicit Identities")]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("unexpected")]
    [InlineData("wrong-provider")]
    [InlineData("wrong-entry")]
    [InlineData("consumer-changed")]
    [InlineData("process")]
    public void BindsFrozenProvider(string mode)
    {
        var manifest = new ExtensionManifestAdmission(new()).ReadCurrent(JsonSerializer.SerializeToUtf8Bytes(Manifest()), "consumer");
        var step = Step();
        if (mode == "missing") step = step with { Provider = null };
        if (mode == "unexpected") manifest = manifest with { Specialized = manifest.Specialized! with { ExecutionProvider = null } };
        if (mode == "wrong-provider") step = step with { Provider = step.Provider! with { ExtensionId = "compiler" } };
        if (mode == "wrong-entry") step = step with { Provider = step.Provider! with { EntryId = "other" } };
        if (mode == "consumer-changed") step = step with { ExtensionId = "driver" };
        if (mode == "process") step = step with { EngineKind = "process" };
        if (mode == "valid") ExecutionProviderRelationship.Require(manifest, step);
        else Assert.Throws<IOException>(() => ExecutionProviderRelationship.Require(manifest, step));
    }

    private static ExecutionPlanStep Step()
    {
        const string id = "00000000-0000-4000-8000-000000000001";
        var hash = new string('a', 64);
        var identity = new FileTreePhysicalIdentity { VolumeSerial = "0000000000000001", FileId = "00000000000000000000000000000001" };
        var image = new ExecutionPlanInterpreterImage { Path = "D:\\fixture\\worker.exe", SizeBytes = 1, Sha256 = hash,
            Identity = identity, ParentIdentity = identity, RuntimeFiles = [] };
        return new ExecutionPlanStep
        {
            StepId = "fixture", ScriptId = id, EngineKind = "extensionWorker", ExtensionId = "consumer", EntryId = "main", DependsOn = [],
            ExecutablePath = image.Path, ImageSizeBytes = image.SizeBytes, ImageSha256 = hash, ImageIdentity = identity, ParentIdentity = identity,
            Arguments = [], WorkingDirectory = "D:\\fixture", WorkingDirectoryIdentity = identity, Tasks = [], SelectedTaskIds = [],
            UpdateConfigurationAtFinish = true, ConfigurationTargets = [], LogTargets = [],
            Adapter = new() { InstalledRevision = 1, InstalledJournalId = id, Resources = [],
                InstalledFootprint = new() { RelativePath = "blobs/plans/" + hash + ".json", SizeBytes = 1, Sha256 = hash },
                Manifest = new() { RelativePath = "blobs/content/" + hash + ".bin", SizeBytes = 1, Sha256 = hash },
                Script = new() { RelativePath = "blobs/content/" + hash + ".bin", SizeBytes = 1, Sha256 = hash } },
            Provider = new() { ExtensionId = "driver", EntryId = "main", ActivationId = id, Verification = "controlled-local-package",
                Protocol = "nexus.execution.provider/1", PackageRoot = "D:\\fixture", PackageRootIdentity = identity,
                Manifest = new() { RelativePath = "blobs/plans/" + hash + ".json", SizeBytes = 1, Sha256 = hash },
                PackageArchive = image, Worker = image, Upstream = image, UpstreamArguments = [] }
        };
    }

    private static JsonObject Manifest() => new()
    {
        ["format"] = "nexus.extension.manifest", ["version"] = 1, ["extensionId"] = "consumer", ["artifactName"] = "Consumer",
        ["displayName"] = "Consumer", ["packageVersion"] = "0.5.0", ["kind"] = "data-specialized", ["minHostVersion"] = "0.18.0",
        ["sdkVersion"] = "0.1.0", ["contractFingerprint"] = SdkIdentity.ContractFingerprint,
        ["capabilities"] = new JsonArray("task.adapt"), ["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") },
        ["specialized"] = new JsonObject { ["adapterEntry"] = "adapter.js", ["compiler"] = new JsonObject { ["extensionId"] = "compiler", ["compilerId"] = "project" },
            ["executionProvider"] = new JsonObject { ["extensionId"] = "driver", ["entryId"] = "main" } }
    };
}
