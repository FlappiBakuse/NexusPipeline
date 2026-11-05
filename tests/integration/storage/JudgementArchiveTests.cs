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
    [Fact(DisplayName = "Invocation And Probe Archives Share Bounded Content Parts And Bind The Exact Parent Allowance")]
    public void SharesBoundedParts()
    {
        using var source = new PlanBuilderFixture();
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var invocation = Invocation(source.Instance.InstanceId);
        var held = new List<IDisposable>();
        try
        {
            var input = JudgementInvocationArchive.Write(blobs, invocation, Hold);
            var probe = JudgementProbeArchive.Write(blobs, Exchange(invocation), Hold);
            Assert.Equal(invocation.ProbeEvidenceAllowance, JudgementInvocationArchive.Read(blobs, input).ProbeEvidenceAllowance);
            Assert.Equal(invocation.ProbeEvidenceAllowance, JudgementInvocationArchive.Decode(blobs, input).ProbeEvidenceAllowance);
            var archive = JudgementProbeArchive.Read(blobs, probe);
            Assert.True(archive.SizeBytes > 2 * 1024 * 1024);
            Assert.InRange(probe.SizeBytes, 1, 2 * 1024 * 1024);
            Assert.All(archive.Parts, part => Assert.InRange(part.SizeBytes, 1, 1024 * 1024));
            var decoded = JudgementProbeArchive.Decode(blobs, probe);
            JudgementProbeProtocol.RequireExchanges([decoded], invocation);
            Assert.Equal(JudgementProbeProtocol.Hash([Exchange(invocation)]), JudgementProbeProtocol.Hash([decoded]));
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
                    var first = parts[0]!.DeepClone(); var last = parts[parts.Count - 1]!.DeepClone();
                    parts[0] = last; parts[parts.Count - 1] = first; break;
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
            Assert.Equal(before.Rows, source.Snapshot().Rows);
            Assert.Equal(before.Files, source.Snapshot().Files);
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
        var exchanges = Enumerable.Range(1, JudgementProbeProtocol.MaximumCalls).Select(ordinal =>
        {
            var request = new JudgementProbeRequest(JudgementProbeProtocol.RequestFormat, 1, invocation.Identity, invocation.Generation,
                invocation.InputSha256, ordinal, "httpGet", JsonSerializer.SerializeToElement(new { url, maxBytes = 2097152 }));
            JudgementProbeProtocol.Require(request, invocation, ordinal);
            Assert.Equal(url, request.Options.GetProperty("url").GetString());
            Assert.InRange(JudgementWorkerProtocol.Encode(request).Length, JudgementProbeProtocol.MaximumRequestBytes + 1,
                JudgementProbeProtocol.MaximumHttpRequestBytes);
            _ = HttpGetProbe.Read(request.Options);
            var exchange = new JudgementProbeExchange(request, JudgementProbeProtocol.Fault(request, "judgement_probe_budget_exceeded"));
            Assert.True(JudgementWorkerProtocol.Encode(exchange).Length <= JudgementProbeProtocol.FaultExchangeBytes);
            return exchange;
        }).ToArray();
        JudgementProbeProtocol.RequireExchanges(exchanges, invocation);
        Assert.True(exchanges.Sum(exchange => (long)JudgementWorkerProtocol.Encode(exchange).Length) <= invocation.ProbeEvidenceAllowance);
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
