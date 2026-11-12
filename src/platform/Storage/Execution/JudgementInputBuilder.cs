using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class JudgementInputBuilder(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    JudgementLogReader? liveLog = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal JudgementWorkerInvocation Build(ExecutionPlan total, JudgementWorkerIdentity identity, JudgementInvocation invocation, BlobReference rawOutput)
        => Prepare(total, identity, rawOutput, invocation.IsFinal).Bind(invocation);

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

    internal void VerifyHistorical(ExecutionPlan total, JudgementWorkerInvocation invocation, BlobReference rawOutput)
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
        var log = ReadLog(identity, rawOutput, invocation.IsFinal);
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
