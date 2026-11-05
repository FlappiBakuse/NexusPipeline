using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Execution.Judgement;

internal sealed record JudgementWorkerIdentity(StepEvidenceBinding Primary, string WorkerJobName, Guid OwnerId);
internal sealed record JudgementInterpreterImage(string Path, long SizeBytes, string Sha256, PhysicalObjectIdentity Identity,
    PhysicalObjectIdentity ParentIdentity, IReadOnlyList<JudgementRuntimeFile> RuntimeFiles);
internal sealed record JudgementRuntimeFile(string RelativePath, long SizeBytes, string Sha256, PhysicalObjectIdentity Identity,
    PhysicalObjectIdentity ParentIdentity);
internal sealed record JudgementWorkerInitialization(string Format, int Version, JudgementWorkerIdentity Identity,
    string Language, string Script, string ScriptSha256, JudgementInterpreterImage Broker, JudgementInterpreterImage? Python,
    string WorkingDirectory, PhysicalObjectIdentity WorkingDirectoryIdentity, int InvocationTimeoutSeconds, int MaximumInvocations,
    long MaximumEvidenceBytes = 64L * 1024 * 1024);
internal sealed record JudgementWorkerInvocation(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, bool IsFinal, JsonElement Input, string InputSha256)
{
    public long ProbeEvidenceAllowance { get; init; } = 2L * 1024 * 1024;
}
internal sealed record JudgementPythonRequest(string Script, JsonElement Input, JudgementWorkerIdentity Identity,
    long Generation, string InputSha256, long ProbeEvidenceAllowance);
internal sealed record JudgementWorkerResponse(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, bool IsFinal, string InputSha256, BusinessJudgementResult? Result, string? FaultCode)
{
    public IReadOnlyList<JudgementConsoleEntry> Console { get; init; } = [];
    public int ProbeCount { get; init; }
    public string ProbeTranscriptSha256 { get; init; } = JudgementProbeProtocol.Hash([]);
}
internal sealed record JudgementPythonResponse(BusinessJudgementResult? Result, string? FaultCode, IReadOnlyList<JudgementConsoleEntry> Console);
internal sealed record JudgementWorkerReady(string Format, int Version, JudgementWorkerIdentity Identity, ProcessIdentity Process);
internal sealed record WorkerChildReady(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, ProcessIdentity Process);
internal sealed record WorkerChildAdmission(string Format, int Version, JudgementWorkerIdentity Identity,
    long Generation, ProcessIdentity Process);

internal static class JudgementWorkerProtocol
{
    internal const int MaximumInvocationBytes = 32 * 1024 * 1024;
    internal const int MaximumLogBytes = 4 * 1024 * 1024;
    internal const int MaximumResponseBytes = 128 * 1024;
    internal const long MaximumEvidenceBytes = 64L * 1024 * 1024;
    internal const string InitializationFormat = "nexus.execution.judgement-initialization";
    internal const string InvocationFormat = "nexus.execution.judgement-invocation";
    internal const string ResponseFormat = "nexus.execution.judgement-response";
    internal const string ReadyFormat = "nexus.execution.judgement-ready";
    internal const string ChildReadyFormat = "nexus.execution.judgement-child-ready";
    internal const string ChildAdmissionFormat = "nexus.execution.judgement-child-admission";
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<BusinessOutcome>(JsonNamingPolicy.CamelCase, false) }
    };

    internal static int FrameLimit(Type type) => type == typeof(JudgementWorkerInvocation) || type == typeof(JudgementPythonRequest)
        ? MaximumInvocationBytes : type == typeof(JudgementProbeReply) || type == typeof(JudgementProbeExchange)
            ? JudgementProbeProtocol.MaximumFrameBytes : StrictJson.MaximumBytes;

    internal static byte[] Encode<T>(T value) => BoundedJson.Serialize(value, Options, FrameLimit(typeof(T)));

    internal static T Decode<T>(ReadOnlyMemory<byte> bytes)
    {
        var limit = FrameLimit(typeof(T));
        var strings = typeof(T) == typeof(JudgementWorkerInvocation) || typeof(T) == typeof(JudgementPythonRequest)
            ? MaximumLogBytes : typeof(T) == typeof(JudgementProbeReply) || typeof(T) == typeof(JudgementProbeExchange)
                ? JudgementProbeProtocol.MaximumBodyStringBytes : StrictJson.MaximumStringBytes;
        using var document = StrictJson.Parse(bytes, limit, strings);
        var value = document.RootElement.Deserialize<T>(Options) ?? throw Invalid();
        if (value is JudgementWorkerInvocation invocation)
        {
            var empty = JsonSerializer.SerializeToElement(new { });
            var envelope = document.RootElement.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Name == "input" ? empty : property.Value, StringComparer.Ordinal);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(envelope),
                JsonSerializer.SerializeToElement(invocation with { Input = empty }, Options))) throw Invalid();
        }
        else if (value is JudgementProbeReply reply)
        {
            if (!JsonElement.DeepEquals(ProbeEnvelope(document.RootElement), JsonSerializer.SerializeToElement(
                reply with { Result = reply.Result.HasValue ? JsonSerializer.SerializeToElement(new { }) : null }, Options))) throw Invalid();
        }
        else if (value is JudgementProbeExchange exchange)
        {
            if (exchange.Request is null || exchange.Reply is null) throw Invalid();
            var envelope = document.RootElement.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Name == "reply" ? ProbeEnvelope(property.Value) : property.Value, StringComparer.Ordinal);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(envelope), JsonSerializer.SerializeToElement(
                exchange with { Reply = exchange.Reply with { Result = exchange.Reply.Result.HasValue ? JsonSerializer.SerializeToElement(new { }) : null } }, Options))) throw Invalid();
        }
        else if (!JsonElement.DeepEquals(document.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        return value;
    }

    internal static void Require(JudgementWorkerInitialization value)
    {
        RequireIdentity(value.Identity);
        if (value.Format != InitializationFormat || value.Version != 1 || value.Language is not ("javascript" or "python") ||
            value.Script is null || Utf8.GetByteCount(value.Script) is < 1 or > 262144 || Hash(Utf8.GetBytes(value.Script)) != value.ScriptSha256 ||
            value.WorkingDirectory is null || !Path.IsPathFullyQualified(value.WorkingDirectory) || !Physical(value.WorkingDirectoryIdentity) ||
            value.InvocationTimeoutSeconds is < 1 or > 300 || value.MaximumInvocations is < 1 or > 4095 ||
            value.MaximumEvidenceBytes is < MaximumInvocationBytes or > MaximumEvidenceBytes ||
            (value.Language == "python") != (value.Python is not null)) throw Invalid();
        RequireImage(value.Broker); if (value.Python is not null) RequireImage(value.Python);
    }

    internal static void Require(JudgementWorkerInvocation value, JudgementWorkerIdentity identity, long expectedGeneration)
    {
        RequireIdentity(value.Identity);
        if (value.Format != InvocationFormat || value.Version != 1 || value.Identity != identity || value.Generation != expectedGeneration ||
            value.Generation is < 1 or > 9007199254740991 || value.ProbeEvidenceAllowance < JudgementProbeProtocol.FaultReserveBytes ||
            value.ProbeEvidenceAllowance > JudgementProbeProtocol.MaximumEvidenceBytes || value.Input.ValueKind != JsonValueKind.Object ||
            InputHash(value.Input) != value.InputSha256) throw Invalid();
    }

    internal static void Require(JudgementWorkerResponse value, JudgementWorkerInvocation invocation) =>
        Require(value, invocation.Identity, invocation.Generation, invocation.IsFinal, invocation.InputSha256);

    internal static void Require(JudgementWorkerResponse value, JudgementWorkerIdentity identity, long generation, bool isFinal, string inputSha256)
    {
        if (value.Format != ResponseFormat || value.Version != 1 || value.Identity != identity || value.Generation != generation ||
            value.IsFinal != isFinal || value.InputSha256 != inputSha256 ||
            (value.Result is null) == (value.FaultCode is null)) throw Invalid();
        if (value.ProbeCount is < 0 or > JudgementProbeProtocol.MaximumCalls || !FileTreePolicy.Digest(value.ProbeTranscriptSha256) ||
            value.ProbeCount == 0 && value.ProbeTranscriptSha256 != JudgementProbeProtocol.Hash([])) throw Invalid();
        JudgementConsole.Require(value.Console);
        if (Encode(value).Length > MaximumResponseBytes) throw Invalid();
        if (value.Result is { } result) RequireResult(result);
        if (value.FaultCode is { } fault && (fault.Length is < 1 or > 80 || fault.Any(character =>
            character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))) throw Invalid();
    }

    internal static BusinessJudgementResult ReadResult(ReadOnlyMemory<byte> bytes)
    {
        var result = Decode<BusinessJudgementResult>(bytes); RequireResult(result); return result;
    }

    internal static JudgementPythonResponse ReadPythonResponse(ReadOnlyMemory<byte> bytes)
    {
        var value = Decode<JudgementPythonResponse>(bytes);
        if ((value.Result is null) == (value.FaultCode is null) || value.FaultCode is not (null or "judgement_worker_execution_failed")) throw Invalid();
        JudgementConsole.Require(value.Console);
        if (value.Result is { } result) RequireResult(result);
        if (bytes.Length > MaximumResponseBytes) throw Invalid();
        return value;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string InputHash(JsonElement input)
    {
        using var output = new InputHashStream(MaximumInvocationBytes);
        JsonSerializer.Serialize(output, input);
        return output.Finish();
    }

    private static JsonElement ProbeEnvelope(JsonElement root)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return JsonSerializer.SerializeToElement(root.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Name == "result" && property.Value.ValueKind != JsonValueKind.Null ? empty : property.Value, StringComparer.Ordinal));
    }

    private static void RequireResult(BusinessJudgementResult result)
    {
        if (result.Outcome is not (BusinessOutcome.Unverified or BusinessOutcome.Succeeded or BusinessOutcome.Partial or BusinessOutcome.Failed) ||
            result.Reason is null || result.Reason.Length > 4096 || result.Outcome != BusinessOutcome.Unverified && string.IsNullOrWhiteSpace(result.Reason)) throw Invalid();
    }

    internal static void RequireIdentity(JudgementWorkerIdentity value)
    {
        if (value is null || value.OwnerId == Guid.Empty || value.Primary is not { } binding || binding.InstanceId == Guid.Empty ||
            binding.ParentJournalId == Guid.Empty || binding.ParentOperationId == Guid.Empty || binding.RunId == Guid.Empty || binding.AttemptId == Guid.Empty ||
            binding.WriterId == Guid.Empty || binding.SessionId < 0 || binding.StepId is null || binding.StepId.Length is < 1 or > 120 ||
            binding.StepId.Any(char.IsControl) || !Job(binding.JobName) || !Job(value.WorkerJobName) || binding.JobName == value.WorkerJobName ||
            !FileTreePolicy.Digest(binding.PlanDigest)) throw Invalid();
    }

    private static void RequireImage(JudgementInterpreterImage value)
    {
        if (value is null || value.Path is null || !Path.IsPathFullyQualified(value.Path) || value.SizeBytes is < 1 or > 1073741824 ||
            !FileTreePolicy.Digest(value.Sha256) || !Physical(value.Identity) || !Physical(value.ParentIdentity) ||
            value.RuntimeFiles is null || value.RuntimeFiles.Count > JudgementRuntimeAssets.MaximumFiles) throw Invalid();
        if (value.RuntimeFiles.Any(file => file is null) ||
            value.RuntimeFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.RuntimeFiles.Count) throw Invalid();
        string? prior = null;
        foreach (var file in value.RuntimeFiles)
        {
            if (file is null || file.SizeBytes is < 1 or > 1073741824 || !FileTreePolicy.Digest(file.Sha256) ||
                !Physical(file.Identity) || !Physical(file.ParentIdentity)) throw Invalid();
            PhysicalPathPolicy.ValidateRelativePath(file.RelativePath);
            if (prior is not null && StringComparer.Ordinal.Compare(prior, file.RelativePath) >= 0) throw Invalid();
            prior = file.RelativePath;
        }
    }

    private static bool Physical(PhysicalObjectIdentity? identity) => identity is not null &&
        Hex(identity.VolumeSerial, 16) && Hex(identity.FileId, 32);
    private static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Job(string? value) => value is { Length: 50 } && value.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) &&
        Guid.TryParseExact(value[18..], "N", out var id) && id != Guid.Empty && value[18..] == id.ToString("N");
    private static JsonException Invalid() => new("judgement_worker_protocol_unrecognized");
}
