using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.ContractTests;

internal sealed class TaskScriptFixture : IAsyncDisposable
{
    internal const string Response = "globalThis.nexusTaskAdapter = input => ({...input, direction:'response', resources:undefined, tasks:[]});";
    internal ContractReader Reader { get; } = new();
    internal ExtensionScope Scope { get; private set; } = new("sample-adapter");
    internal TaskAdapter Request { get; set; } = new()
    {
        Format = "nexus.task.adapter", Version = 1, Direction = "request", Phase = "discover", ExtensionId = "sample-adapter",
        OperationId = Guid.NewGuid().ToString("D"), Resources = []
    };
    internal ExtensionManifest Manifest { get; set; } = new()
    {
        Format = "nexus.extension.manifest", Version = 1, ExtensionId = "sample-adapter", ArtifactName = "SampleAdapter", DisplayName = "SampleAdapter",
        PackageVersion = "0.5.0", Kind = "data-specialized", MinHostVersion = "0.18.0", SdkVersion = SdkIdentity.Version,
        ContractFingerprint = SdkIdentity.ContractFingerprint, Capabilities = ["task.adapt", "configuration.edit"], Permissions = new(),
        Specialized = new() { AdapterEntry = "adapter/main.js" }
    };
    internal TaskMessageIdentity Identity => new(Request.ExtensionId, Guid.Parse(Request.OperationId), Request.Phase,
        Request.RunId is null ? null : Guid.Parse(Request.RunId), Request.AttemptId is null ? null : Guid.Parse(Request.AttemptId), Request.PlanDigest);

    internal async Task ActivateAsync()
    {
        await Scope.DisposeAsync();
        Scope = await new ExtensionActivator(Reader).ActivateSpecializedAsync(JsonSerializer.SerializeToUtf8Bytes(Manifest), "sample-adapter",
            static (_, _, _) => ValueTask.CompletedTask, default);
    }

    internal TaskAdapterExecutor Executor(string script = Response) => new(JsonSerializer.SerializeToUtf8Bytes(Manifest), Scope, Reader,
        "adapter/main.js", script, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script))));
    internal TaskAdapter Execute(TaskAdapterExecutor executor, CancellationToken token = default)
        => executor.Execute(JsonSerializer.SerializeToUtf8Bytes(Request), Identity, token);
    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}
