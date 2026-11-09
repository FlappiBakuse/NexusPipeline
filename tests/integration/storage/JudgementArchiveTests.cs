using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class JudgementArchiveTests
{
    [Fact(DisplayName = "Large Judgement Inputs And Probe Results Restore With The Parent Allowance")]
    public void RestoresArchivedEvidence()
    {
        using var source = new PlanBuilderFixture();
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var log = JsonSerializer.SerializeToElement(new { log = new string('x', 2 * 1024 * 1024) });
        var invocation = Invocation(source.Instance.InstanceId) with { Input = log, InputSha256 = JudgementWorkerProtocol.InputHash(log) };
        var held = new List<IDisposable>();
        try
        {
            var restored = JudgementInvocationArchive.Decode(blobs, JudgementInvocationArchive.Write(blobs, invocation, Hold));
            var exchange = Exchange(invocation);
            var probe = JudgementProbeArchive.Write(blobs, exchange, Hold);
            var decoded = JudgementProbeArchive.Decode(blobs, probe);
            Assert.True(JsonElement.DeepEquals(invocation.Input, restored.Input));
            Assert.Equal(invocation.ProbeEvidenceAllowance, restored.ProbeEvidenceAllowance);
            JudgementProbeProtocol.RequireExchanges([decoded], restored);
            Assert.True(JsonElement.DeepEquals(exchange.Request.Options, decoded.Request.Options));
            Assert.True(JsonElement.DeepEquals(exchange.Reply.Result!.Value, decoded.Reply.Result!.Value));
        }
        finally { foreach (var lease in held) lease.Dispose(); }
        void Hold(BlobReference reference) => held.Add(blobs.OpenVerified(reference));
    }

    [Theory(DisplayName = "Unknown Or Mismatched Archive Metadata Cannot Change An Existing Frame Or Consume Any Database State")]
    [InlineData("generation")]
    [InlineData("ordinal")]
    [InlineData("hash")]
    [InlineData("part-order")]
    [InlineData("missing-part")]
    [InlineData("allowance-mismatch")]
    public void RejectsMismatchedArchives(string fault)
    {
        using var source = new PlanBuilderFixture();
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var invocation = Invocation(source.Instance.InstanceId);
        var held = new List<IDisposable>();
        try
        {
            var isInput = fault == "allowance-mismatch";
            var reference = isInput ? JudgementInvocationArchive.Write(blobs, invocation, Hold) :
                JudgementProbeArchive.Write(blobs, Exchange(invocation), Hold);
            var node = JsonNode.Parse(JudgementEvidenceCodec.ReadBytes(blobs, reference))!.AsObject();
            switch (fault)
            {
                case "generation": node["generation"] = 2; break;
                case "ordinal": node["ordinal"] = 2; break;
                case "hash": node["sha256"] = new string('b', 64); break;
                case "part-order":
                    var parts = node["parts"]!.AsArray();
                    var first = parts[0]!.DeepClone();
                    parts[0] = parts[1]!.DeepClone(); parts[1] = first; break;
                case "missing-part": node["parts"]!.AsArray().RemoveAt(0); break;
                case "allowance-mismatch": node["probeEvidenceAllowance"] = 15 * 1024 * 1024; break;
            }
            var damaged = blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(node)); Hold(damaged);
            var before = source.Snapshot();
            Assert.Throws<IOException>(() =>
            {
                if (isInput) _ = JudgementInvocationArchive.Decode(blobs, damaged);
                else _ = JudgementProbeArchive.Decode(blobs, damaged);
            });
            var after = source.Snapshot();
            Assert.Equal(before.Rows, after.Rows);
            Assert.Equal(before.Files, after.Files);
        }
        finally { foreach (var lease in held) lease.Dispose(); }
        void Hold(BlobReference reference) => held.Add(blobs.OpenVerified(reference));
    }

    [Fact(DisplayName = "A Periodic Call Reserves One Final Input And Fault Capacity Without Double Reserving Large Probe Bodies")]
    public void ReservesFinalInput()
    {
        var maximum = JudgementWorkerProtocol.MaximumEvidenceBytes;
        const long input = 1024 * 1024;
        Assert.Equal(JudgementProbeProtocol.MaximumEvidenceBytes, JudgementEvidenceBudget.AllocateProbes(maximum, 0, input, false));
        var recorded = maximum - JudgementWorkerProtocol.MaximumInvocationBytes - 2L * JudgementWorkerProtocol.MaximumResponseBytes -
            2L * JudgementProbeProtocol.FaultReserveBytes - input;
        Assert.Equal(input, JudgementEvidenceBudget.MaximumInputBytes(maximum, recorded, false));
        Assert.Equal(JudgementProbeProtocol.FaultReserveBytes, JudgementEvidenceBudget.AllocateProbes(maximum, recorded, input, false));
        Assert.Equal(0, JudgementEvidenceBudget.AllocateProbes(maximum, recorded, input + 1, false));
        var after = recorded + input + JudgementWorkerProtocol.MaximumResponseBytes + JudgementProbeProtocol.FaultReserveBytes;
        Assert.Equal(JudgementWorkerProtocol.MaximumInvocationBytes, JudgementEvidenceBudget.MaximumInputBytes(maximum, after, true));
        Assert.Equal(JudgementProbeProtocol.FaultReserveBytes, JudgementEvidenceBudget.AllocateProbes(
            maximum, after, JudgementWorkerProtocol.MaximumInvocationBytes, true));
    }

    [Fact(DisplayName = "A Full Unicode Http Url Has An Explicit Request Bound And Every Declared Quota Fault Fits Its Reserve")]
    public void BoundsRequestUrls()
    {
        var invocation = Invocation(Guid.NewGuid()) with { ProbeEvidenceAllowance = JudgementProbeProtocol.FaultReserveBytes };
        const string prefix = "https://controlled.invalid/";
        var url = prefix + new string('界', 2048 - prefix.Length);
        var options = JsonSerializer.SerializeToElement(new { url, maxBytes = HttpGetProbe.MaximumBodyBytes });
        Assert.Equal(url, HttpGetProbe.Read(options).Destination.OriginalString);
        Assert.Throws<JsonException>(() => HttpGetProbe.Read(JsonSerializer.SerializeToElement(new { url = url + "界" })));
        var exchanges = Enumerable.Range(1, JudgementProbeProtocol.MaximumCalls).Select(ordinal =>
        {
            var request = new JudgementProbeRequest(JudgementProbeProtocol.RequestFormat, 1, invocation.Identity, invocation.Generation,
                invocation.InputSha256, ordinal, "httpGet", options);
            return new JudgementProbeExchange(request, JudgementProbeProtocol.Fault(request, "judgement_probe_budget_exceeded"));
        }).ToArray();
        JudgementProbeProtocol.RequireExchanges(exchanges, invocation);
        Assert.All(exchanges, exchange => Assert.True(JudgementWorkerProtocol.Encode(exchange).Length <= JudgementProbeProtocol.FaultExchangeBytes));
    }

    private static JudgementWorkerInvocation Invocation(Guid instanceId)
    {
        var binding = new StepEvidenceBinding(instanceId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "binding-" + Guid.NewGuid().ToString("D"), "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), 1, new string('a', 64));
        var identity = new JudgementWorkerIdentity(binding, "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), Guid.NewGuid());
        var input = JsonSerializer.SerializeToElement(new { });
        return new(JudgementWorkerProtocol.InvocationFormat, 1, identity, 1, true, input, JudgementWorkerProtocol.InputHash(input))
            { ProbeEvidenceAllowance = JudgementProbeProtocol.MaximumEvidenceBytes };
    }

    private static JudgementProbeExchange Exchange(JudgementWorkerInvocation invocation)
    {
        var request = new JudgementProbeRequest(JudgementProbeProtocol.RequestFormat, 1, invocation.Identity, invocation.Generation,
            invocation.InputSha256, 1, "httpGet", JsonSerializer.SerializeToElement(new { url = "https://controlled.invalid/body", maxBytes = 2097152 }));
        var observed = JsonSerializer.SerializeToElement(new HttpGetObservation(true, 200, new string('<', 512 * 1024), false, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new(request, JudgementProbeProtocol.Success(request, observed));
    }
}
