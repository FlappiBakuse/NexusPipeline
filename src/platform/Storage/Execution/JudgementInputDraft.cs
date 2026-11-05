using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class JudgementInputDraft
{
    private readonly JudgementWorkerIdentity _identity;
    private readonly JsonElement _input;
    internal long MaximumEncodedBytes { get; }
    internal long StableMinimumEncodedBytes { get; }

    internal JudgementInputDraft(JudgementWorkerIdentity identity, JsonElement input, long? encodedInputBytes = null)
    {
        _identity = identity; _input = input.Clone();
        var size = encodedInputBytes ?? Size(_input);
        MaximumEncodedBytes = checked(EnvelopeSize(4095) + size - Size(_input.GetProperty("generation")) + 4
            - Size(_input.GetProperty("isFinal")) + 5);
        var stable = size - Size(_input.GetProperty("generation")) + 1;
        foreach (var field in new[] { "files", "locale", "screenshots" })
            if (_input.TryGetProperty(field, out var value)) stable = checked(stable - Size(value) + 2);
        StableMinimumEncodedBytes = checked(EnvelopeSize(1, JudgementProbeProtocol.FaultReserveBytes) + stable);
    }

    internal JudgementWorkerInvocation Bind(JudgementInvocation invocation)
    {
        if (invocation.AttemptId != _identity.Primary.AttemptId || invocation.OwnerId != _identity.OwnerId || invocation.Generation is < 1 or > 4095)
            throw new IOException("execution_judgement_input_unbound");
        return Create(invocation.Generation, invocation.IsFinal);
    }

    private JudgementWorkerInvocation Create(long generation, bool final)
    {
        var values = _input.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        values["generation"] = JsonSerializer.SerializeToElement(generation); values["isFinal"] = JsonSerializer.SerializeToElement(final);
        var bytes = BoundedJson.Serialize(values, null, JudgementWorkerProtocol.MaximumInvocationBytes);
        using var document = StrictJson.Parse(bytes, JudgementWorkerProtocol.MaximumInvocationBytes, JudgementWorkerProtocol.MaximumLogBytes);
        var input = document.RootElement.Clone();
        var invocation = new JudgementWorkerInvocation(JudgementWorkerProtocol.InvocationFormat, 1, _identity, generation, final, input,
            JudgementWorkerProtocol.InputHash(input));
        JudgementWorkerProtocol.Require(invocation, _identity, generation);
        return invocation;
    }

    private long EnvelopeSize(long generation, long allowance = JudgementProbeProtocol.MaximumEvidenceBytes)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return JudgementWorkerProtocol.Encode(new JudgementWorkerInvocation(JudgementWorkerProtocol.InvocationFormat, 1,
            _identity, generation, false, empty, new string('0', 64)) { ProbeEvidenceAllowance = allowance }).LongLength - 2;
    }

    private static long Size(JsonElement value) => BoundedJson.Serialize(value, null, JudgementWorkerProtocol.MaximumInvocationBytes).LongLength;
}
