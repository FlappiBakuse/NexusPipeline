using System.Runtime.Versioning;
using NexusPipeline.Core.Settings;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedExecutionNetwork : IDisposable
{
    internal ProxyPreferences Proxy { get; }
    internal PreparedProxyCredential Credential { get; }
    private readonly Guid _instanceId, _operationId;

    internal PreparedExecutionNetwork(ExecutionPlan plan, Guid operationId, PreparedProxyCredential credential)
    {
        var wire = plan.JudgementNetwork ?? throw new IOException("execution_judgement_network_required");
        credential.VerifyHeld(Guid.ParseExact(plan.InstanceId, "D"), operationId);
        if (credential.Reference != JudgementNetworkBinding.Reference(wire)) throw new InvalidOperationException("secret_revision_conflict");
        Proxy = JudgementNetworkBinding.Proxy(wire); Credential = credential;
        _instanceId = Guid.ParseExact(plan.InstanceId, "D"); _operationId = operationId;
    }

    internal async Task<JudgementProbeReply> ObserveAsync(JudgementProbeRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Credential.VerifyHeld(_instanceId, _operationId);
        if (request.Method != "httpGet") return await JudgementNativeProbes.ObserveAsync(request, cancellation).ConfigureAwait(false);
        HttpClient? client = null;
        if (Proxy.Mode == ProxyMode.Http && !string.IsNullOrWhiteSpace(Proxy.UserName) && Credential.IsConfigured)
            Credential.Use(value =>
            {
                using var material = new SecretValue(value);
                client = ProbeHttpClient.Create(Proxy, material);
            });
        else client = ProbeHttpClient.Create(Proxy, null);
        using (client)
            return await JudgementNativeProbes.ObserveAsync(request, cancellation, client).ConfigureAwait(false);
    }

    public void Dispose() => Credential.Dispose();
}
