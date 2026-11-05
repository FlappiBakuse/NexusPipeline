using System.Security.Cryptography;
using System.Text.Json;

namespace NexusPipeline.Platform.Execution.Judgement;

internal sealed record JudgementProbeRequest(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, string InputSha256, int Ordinal, string Method, JsonElement Options);
internal sealed record JudgementProbeReply(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, string InputSha256, int Ordinal, JsonElement? Result, string? FaultCode);
internal sealed record JudgementProbeExchange(JudgementProbeRequest Request, JudgementProbeReply Reply);

internal static class JudgementProbeProtocol
{
    internal const string RequestFormat = "nexus.execution.judgement-probe-request";
    internal const string ReplyFormat = "nexus.execution.judgement-probe-reply";
    internal const int MaximumCalls = 16;
    internal const int MaximumRequestBytes = 8192;
    internal const int MaximumHttpRequestBytes = 16 * 1024;
    internal const int MaximumFrameBytes = 16 * 1024 * 1024;
    internal const int MaximumBodyStringBytes = Networking.HttpGetProbe.MaximumBodyBytes * 3;
    internal const int MaximumEvidenceBytes = 16 * 1024 * 1024;
    internal const int FaultExchangeBytes = MaximumHttpRequestBytes + 4096 + 64;
    internal const int FaultReserveBytes = MaximumCalls * FaultExchangeBytes;

    internal static void Require(JudgementProbeRequest request, JudgementWorkerInvocation invocation, int ordinal)
        => Require(request, invocation.Identity, invocation.Generation, invocation.InputSha256, ordinal);

    internal static void Require(JudgementProbeRequest request, JudgementWorkerIdentity identity, long generation, string inputSha256, int ordinal)
    {
        if (request is null || request.Format != RequestFormat || request.Version != 1 || request.Identity != identity ||
            request.Generation != generation || request.InputSha256 != inputSha256 ||
            request.Ordinal != ordinal || ordinal is < 1 or > MaximumCalls ||
            request.Method is not ("processes" or "windows" or "httpGet") || request.Options.ValueKind != JsonValueKind.Object ||
            JudgementWorkerProtocol.Encode(request).Length > (request.Method == "httpGet" ? MaximumHttpRequestBytes : MaximumRequestBytes))
            throw Invalid();
    }

    internal static void Require(JudgementProbeReply reply, JudgementProbeRequest request)
    {
        if (reply is null || request is null || reply.Format != ReplyFormat || reply.Version != 1 || reply.Identity != request.Identity ||
            reply.Generation != request.Generation || reply.InputSha256 != request.InputSha256 || reply.Ordinal != request.Ordinal ||
            (reply.Result is null) == (reply.FaultCode is null) ||
            reply.Result is { } result && result.ValueKind != JsonValueKind.Object ||
            reply.FaultCode is not (null or "judgement_probe_unavailable" or "judgement_probe_options_invalid" or "judgement_probe_budget_exceeded"))
            throw Invalid();
        if (reply.FaultCode is not null && JudgementWorkerProtocol.Encode(reply).Length > 4096) throw Invalid();
        if (reply.Result is { } observed)
        {
            if (request.Method == "httpGet") RequireHttp(observed);
            else
            {
                using var parsed = Serialization.StrictJson.Parse(JudgementWorkerProtocol.Encode(reply));
            }
        }
    }

    internal static JudgementProbeReply Success(JudgementProbeRequest request, JsonElement result) =>
        new(ReplyFormat, 1, request.Identity, request.Generation, request.InputSha256, request.Ordinal, result.Clone(), null);

    internal static JudgementProbeReply Fault(JudgementProbeRequest request, string code) =>
        new(ReplyFormat, 1, request.Identity, request.Generation, request.InputSha256, request.Ordinal, null, code);

    internal static void RequireExchanges(IReadOnlyList<JudgementProbeExchange> exchanges, JudgementWorkerInvocation invocation)
        => RequireExchanges(exchanges, invocation.Identity, invocation.Generation, invocation.InputSha256, invocation.ProbeEvidenceAllowance);

    internal static void RequireExchanges(IReadOnlyList<JudgementProbeExchange> exchanges, JudgementWorkerIdentity identity, long generation, string inputSha256,
        long allowance)
    {
        if (allowance is < FaultReserveBytes or > MaximumEvidenceBytes || exchanges.Count > MaximumCalls) throw Invalid();
        long size = 0;
        for (var index = 0; index < exchanges.Count; index++)
        {
            var exchange = exchanges[index];
            Require(exchange.Request, identity, generation, inputSha256, index + 1); Require(exchange.Reply, exchange.Request);
            size = checked(size + JudgementWorkerProtocol.Encode(exchange).LongLength);
            if (size > allowance) throw Invalid();
        }
    }

    internal static string Hash(IReadOnlyList<JudgementProbeExchange> exchanges)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var exchange in exchanges) hash.AppendData(JudgementWorkerProtocol.Encode(exchange));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RequireHttp(JsonElement value)
    {
        if (value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "body", "error", "ok", "status", "truncated" }) is false ||
            value.GetProperty("ok").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetProperty("truncated").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetProperty("status").ValueKind != JsonValueKind.Number || !value.GetProperty("status").TryGetInt32(out var status) ||
            value.GetProperty("body").ValueKind != JsonValueKind.String ||
            System.Text.Encoding.UTF8.GetByteCount(value.GetProperty("body").GetString()!) > MaximumBodyStringBytes) throw Invalid();
        var error = value.GetProperty("error");
        if (error.ValueKind == JsonValueKind.Null)
        {
            if (status is < 100 or > 999 || value.GetProperty("ok").GetBoolean() != (status is >= 200 and <= 299)) throw Invalid();
        }
        else if (error.ValueKind != JsonValueKind.String || error.GetString() is not ("timeout" or "network_error") || status != 0 ||
            value.GetProperty("ok").GetBoolean() || value.GetProperty("truncated").GetBoolean() || value.GetProperty("body").GetString() != "") throw Invalid();
    }

    private static IOException Invalid() => new("judgement_probe_protocol_unrecognized");
}
