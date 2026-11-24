using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Text;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class JudgementInputBuilder(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    JudgementLogReader? liveLog = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal JudgementInputDraft Prepare(ExecutionPlan total, JudgementWorkerIdentity identity, BlobReference rawOutput, bool final = false)
    {
        if (total.InstanceId != instance.InstanceId.ToString("D") || total.RunId != identity.Primary.RunId.ToString("D"))
            throw new IOException("execution_judgement_input_unbound");
        var step = total.Steps.Single(item => item.StepId == identity.Primary.StepId);
        var bundle = new ExecutionJudgementInputs(instance, contracts).ReadBundle(total);
        var script = bundle.Scripts.Single(item => item.Id == step.ScriptId);
        var settings = new HostSettingsStore(database).Read();
        if (settings.Revision != total.SettingsRevision) throw new IOException("execution_judgement_settings_changed");
        var log = ReadLog(identity, rawOutput, final, liveLog);
        var files = JudgementFileInventory.Read(step);
        var inputBytes = BoundedJson.Serialize(new
        {
            format = "nexus.execution.judgement-input", version = 1, identity, generation = 4095, isFinal = false,
            script, user = bundle.Users.SingleOrDefault(user => user.Id == step.UserId),
            binding = bundle.Bindings.SingleOrDefault(binding => binding.Id == step.BindingId),
            scriptDir = step.WorkingDirectory, configurations = total.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).ToArray(),
            files, screenshots = Array.Empty<object>(), log = log.Text(), logTruncated = log.IsTruncated,
            locale = settings.Values.Locale == HostLocale.ZhCn ? "zh-CN" : "en-US", rawOutput
        }, Options, JudgementWorkerProtocol.MaximumInvocationBytes);
        using var document = StrictJson.Parse(inputBytes, JudgementWorkerProtocol.MaximumInvocationBytes, JudgementWorkerProtocol.MaximumLogBytes);
        return new(identity, document.RootElement, inputBytes.LongLength);
    }

    internal void VerifyHistorical(ExecutionPlan total, JudgementWorkerInvocation invocation, BlobReference rawOutput,
        JudgementLogReader outputLog)
    {
        var identity = invocation.Identity;
        JudgementWorkerProtocol.Require(invocation, identity, invocation.Generation);
        if (total.InstanceId != instance.InstanceId.ToString("D") || total.RunId != identity.Primary.RunId.ToString("D")) throw Invalid();
        var step = total.Steps.Single(item => item.StepId == identity.Primary.StepId);
        var bundle = new ExecutionJudgementInputs(instance, contracts).ReadBundle(total);
        var input = invocation.Input;
        var expected = new[] { "format", "version", "identity", "generation", "isFinal", "script", "user", "binding", "scriptDir",
            "configurations", "files", "screenshots", "log", "logTruncated", "locale", "rawOutput" };
        if (!input.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw Invalid();
        Equal("format", "nexus.execution.judgement-input"); Equal("version", 1); Equal("identity", identity);
        Equal("generation", invocation.Generation); Equal("isFinal", invocation.IsFinal); Equal("rawOutput", rawOutput);
        Equal("script", bundle.Scripts.Single(item => item.Id == step.ScriptId));
        Equal("user", bundle.Users.SingleOrDefault(user => user.Id == step.UserId));
        Equal("binding", bundle.Bindings.SingleOrDefault(binding => binding.Id == step.BindingId));
        Equal("scriptDir", step.WorkingDirectory);
        Equal("configurations", total.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).ToArray());
        var log = ReadLog(identity, rawOutput, invocation.IsFinal, outputLog);
        if (input.GetProperty("log").GetString() != log.Text() || input.GetProperty("logTruncated").GetBoolean() != log.IsTruncated) throw Invalid();
        if (input.GetProperty("locale").GetString() is not ("zh-CN" or "en-US") ||
            input.GetProperty("screenshots").ValueKind != JsonValueKind.Array || input.GetProperty("screenshots").GetArrayLength() != 0) throw Invalid();
        JudgementFileInventory.VerifySaved(step, input.GetProperty("files"));
        return;

        void Equal(string field, object? value)
        {
            if (!JsonElement.DeepEquals(input.GetProperty(field), JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        }
    }

    private JudgementLogTail ReadLog(JudgementWorkerIdentity identity, BlobReference rawOutput, bool final, JudgementLogReader? reader = null)
    {
        var blobs = new ImmutableBlobStore(instance, contracts);
        var raw = RawOutputCodec.Read(blobs, identity.Primary, rawOutput);
        var context = new StepEvidenceReader(instance, database, contracts).Context(identity.Primary);
        _ = FileLogEvidence.Require(instance, contracts, blobs, context.Total, identity.Primary, context.Job.FileLogBaseline, raw);
        if (reader is not null) return reader.Read(blobs, raw, final);
        var log = new JudgementLogTail();
        if (!RawOutputReplay.Read(blobs, raw, log.AppendLine, includePartial: final)) throw new IOException("execution_judgement_log_unverified");
        return log;
    }

    private static IOException Invalid() => new("execution_judgement_input_unbound");
}

internal sealed class JudgementLogTail
{
    private readonly byte[] _buffer = new byte[JudgementWorkerProtocol.MaximumLogBytes];
    private int _next, _length;
    private long _observed;
    internal bool IsTruncated => _observed > _buffer.Length;

    internal void AppendLine(string line)
    {
        Append(JudgementWorkerProtocol.Utf8.GetBytes(line)); Append("\n"u8);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        _observed = checked(_observed + bytes.Length);
        if (bytes.Length >= _buffer.Length)
        {
            bytes[^_buffer.Length..].CopyTo(_buffer); _next = 0; _length = _buffer.Length; return;
        }
        var first = Math.Min(bytes.Length, _buffer.Length - _next);
        bytes[..first].CopyTo(_buffer.AsSpan(_next)); bytes[first..].CopyTo(_buffer);
        _next = (_next + bytes.Length) % _buffer.Length; _length = Math.Min(_buffer.Length, _length + bytes.Length);
    }

    internal string Text()
    {
        var ordered = new byte[_length];
        var start = (_next - _length + _buffer.Length) % _buffer.Length;
        var first = Math.Min(_length, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(ordered); _buffer.AsSpan(0, _length - first).CopyTo(ordered.AsSpan(first));
        var skip = 0;
        while (skip < ordered.Length && (ordered[skip] & 0xc0) == 0x80) skip++;
        return JudgementWorkerProtocol.Utf8.GetString(ordered.AsSpan(skip));
    }
}

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

internal sealed record JudgementInputFile(string Root, string Path, string AbsolutePath);

internal static class JudgementFileInventory
{
    internal static void VerifySaved(ExecutionPlanStep step, JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 4096) throw Invalid();
        long bytes = 0; string? previous = null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || file.EnumerateObject().Count() != 3 ||
                !file.TryGetProperty("root", out var rootValue) || !file.TryGetProperty("path", out var pathValue) ||
                !file.TryGetProperty("absolutePath", out var absoluteValue)) throw Invalid();
            var root = rootValue.GetString(); var relative = pathValue.GetString(); var absolute = absoluteValue.GetString();
            if (root is not ("script" or "configuration") || relative is null || absolute is null ||
                PhysicalPathPolicy.NormalizeSyntax(absolute) != absolute) throw Invalid();
            PhysicalPathPolicy.ValidateRelativePath(relative);
            var origins = root == "script" ? new[] { step.WorkingDirectory } : step.ConfigurationTargets;
            if (!origins.Any(origin => absolute == origin && Path.GetFileName(origin) == relative ||
                Path.GetFullPath(Path.Combine(origin, relative)) == absolute)) throw Invalid();
            var key = root + "\0" + relative;
            if (previous is not null && StringComparer.Ordinal.Compare(previous, key) > 0) throw Invalid();
            previous = key;
            bytes += Encoding.UTF8.GetByteCount(absolute) + Encoding.UTF8.GetByteCount(relative) + root.Length;
            if (bytes > 1024 * 1024) throw Invalid();
        }
    }

    internal static IReadOnlyList<JudgementInputFile> Read(ExecutionPlanStep step)
    {
        var result = new List<JudgementInputFile>();
        long bytes = 0; var visited = 0;
        void Visit(string root, string path, string origin, int depth = 0)
        {
            if (++visited > 4096 || depth > 128) throw new IOException("execution_judgement_inventory_limit");
            PhysicalPathPolicy.NormalizeWithoutLinks(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("execution_judgement_inventory_link");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Visit(root, child, origin, depth + 1);
            }
            else
            {
                var relative = path == origin ? System.IO.Path.GetFileName(path) : System.IO.Path.GetRelativePath(origin, path).Replace('\\', '/');
                PhysicalPathPolicy.ValidateRelativePath(relative);
                bytes += Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(relative) + root.Length;
                if (result.Count >= 4096 || bytes > 1024 * 1024) throw new IOException("execution_judgement_inventory_limit");
                result.Add(new(root, relative, path));
            }
        }
        foreach (var configuration in step.ConfigurationTargets) Visit("configuration", configuration, configuration);
        Visit("script", step.WorkingDirectory, step.WorkingDirectory);
        return result.OrderBy(item => item.Root, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static IOException Invalid() => new("execution_judgement_inventory_invalid");
}
