using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SpecializedProtocolTests
{
    [Theory(DisplayName = "Original Specialized Packages Execute Four Synthetic Business Phases Through Installed Host Protocol Validation And Reject Revoked Calls")]
    [InlineData("baah")]
    [InlineData("march7th")]
    [InlineData("oknte")]
    [InlineData("okww")]
    [InlineData("zzzonedragon")]
    public async Task UsesPackagedProtocols(string extensionId)
    {
        using var vectorIndex = JsonDocument.Parse(File.ReadAllBytes(Required("NEXUS_SPECIALIZED_PROTOCOL_VECTORS")));
        var input = vectorIndex.RootElement.GetProperty(extensionId);
        var vectorBytes = File.ReadAllBytes(input.GetProperty("path").GetString()!);
        Assert.Equal(input.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(vectorBytes)));
        using var vectors = JsonDocument.Parse(vectorBytes);
        using var packageSet = JsonDocument.Parse(File.ReadAllBytes(Required("NEXUS_PACKAGED_SET")));
        var package = Assert.Single(packageSet.RootElement.GetProperty("runs").EnumerateArray(),
            item => item.GetProperty("label").GetString() == extensionId + "-package");
        var receipt = package.GetProperty("receipt").GetString()!;
        Assert.Equal(package.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(receipt))));
        using var inputs = new BuiltExtensionInputs(extensionId, receipt, explicitPath: true);
        using var execution = new ExecutionAdmissionFixture(saved: false, otherExtensionId: extensionId);
        var source = execution.Source;
        var secrets = new SecretAccessPolicy(source.Instance, source.Database, source.Lease, execution.Resources);
        using var installed = new InstallRuntimeFixture(catalog: execution.CreateCatalog(),
            originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive,
            portDependencies: new(null, null, secrets, null, null, null));
        try
        {
            var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(activation.IsInstalled); Assert.True(activation.IsEnabled);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            ExtensionProjectionPublication.PublishIfDirty(source.Instance, source.Database, source.Lease, new(),
                installed.Roots, execution.Resources, execution.Index);
            var runtime = installed.Activator.RequirePublishedRuntime(extensionId);
            var phases = new HashSet<string>();
            foreach (var vector in vectors.RootElement.EnumerateArray())
            {
                var request = JsonSerializer.Deserialize<TaskAdapter>(vector.GetProperty("input"))!;
                Assert.True(phases.Add(request.Phase));
                Assert.Equal(extensionId, request.ExtensionId);
                using var root = execution.Resources.TryAcquire(Guid.NewGuid(),
                    [new("extension:" + extensionId, ResourceClaimMode.Reference)]).Lease;
                Assert.NotNull(root);
                var result = await runtime.InvokeAdapterAsync(root, () => source.Lease.VerifyHeld(source.Instance), (adapter, current) =>
                {
                    current();
                    Assert.Equal(input.GetProperty("adapterSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(adapter.ReadInputs().Script)));
                    return ValueTask.FromResult(adapter.Execute(Encoding.UTF8.GetBytes(vector.GetProperty("input").GetRawText()),
                        new(request.ExtensionId, Guid.Parse(request.OperationId), request.Phase,
                            request.RunId is null ? null : Guid.Parse(request.RunId),
                            request.AttemptId is null ? null : Guid.Parse(request.AttemptId), request.PlanDigest), default));
                });
                var actual = JsonSerializer.SerializeToNode(result)!;
                foreach (var expectation in vector.GetProperty("expect").EnumerateObject())
                {
                    JsonNode? value = actual;
                    foreach (var step in expectation.Name.Split('.'))
                        value = value is JsonArray array ? array[int.Parse(step, System.Globalization.CultureInfo.InvariantCulture)] : value![step];
                    Assert.True(JsonNode.DeepEquals(value, JsonNode.Parse(expectation.Value.GetRawText())),
                        vector.GetProperty("name").GetString() + ": " + expectation.Name);
                }
            }
            Assert.Equal(new[] { "configuration", "discover", "observe", "retry" }, phases.Order().ToArray());
            await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
            using var retiredRoot = execution.Resources.TryAcquire(Guid.NewGuid(),
                [new("extension:" + extensionId, ResourceClaimMode.Reference)]).Lease;
            Assert.NotNull(retiredRoot);
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.InvokeAdapterAsync(retiredRoot, () => { },
                (_, _) => ValueTask.FromResult(true)).AsTask());
            inputs.VerifyUnchanged();
            Assert.Equal(vectorBytes, File.ReadAllBytes(input.GetProperty("path").GetString()!));
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new IOException(name + "_required");
}
