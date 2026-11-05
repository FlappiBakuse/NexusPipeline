using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record StartRequestDocument(string Format, int Version, Guid OperationId, string SubjectId,
    Guid RunId, Guid JournalId, Guid? RetrySourceOperationId, JsonElement Request);

internal static class StartRequestCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static byte[] Encode(OperationIdentity identity, ExecutionSelectionRequest request, Guid runId,
        Guid journalId, Guid? retrySource = null)
    {
        if (identity != ExecutionStartIdentity.Create(identity.OperationId, identity.SubjectId, request) ||
            runId == Guid.Empty || journalId == Guid.Empty || retrySource == Guid.Empty || retrySource == identity.OperationId)
            throw Invalid();
        using var body = StrictJson.Parse(ExecutionStartIdentity.Encode(request));
        return BoundedJson.Serialize(new StartRequestDocument("nexus.execution.start-source", 1,
            identity.OperationId, identity.SubjectId, runId, journalId, retrySource, body.RootElement.Clone()), Options);
    }

    internal static (StartRequestDocument Document, ExecutionSelectionRequest Request, OperationIdentity Identity) Decode(ReadOnlyMemory<byte> bytes)
    {
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<StartRequestDocument>(Options) ?? throw Invalid();
        if (value.Format != "nexus.execution.start-source" || value.Version != 1) throw Invalid();
        var request = ExecutionStartIdentity.Decode(Encoding.UTF8.GetBytes(value.Request.GetRawText()));
        var identity = ExecutionStartIdentity.Create(value.OperationId, value.SubjectId, request);
        using var expected = StrictJson.Parse(Encode(identity, request, value.RunId, value.JournalId, value.RetrySourceOperationId));
        if (!JsonElement.DeepEquals(json.RootElement, expected.RootElement)) throw Invalid();
        return (value, request, identity);
    }

    internal static IOException Invalid() => new("execution_start_source_unbound");
}
