using System.Text.Json;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Extensibility.State;

namespace Controlled;

internal static class ControlledSecretDocs
{
    internal static async ValueTask<FrontendResult> InvokeAsync(ExtensionActivationContext context, FrontendCall call,
        string directory, CancellationToken cancellation)
    {
        using var input = JsonDocument.Parse(call.Payload); var root = input.RootElement;
        if (call.Operation == "secret-commit")
            return Json(await context.SecretDocuments!.ReadCommitAsync(root.GetProperty("operationId").GetGuid(), cancellation));
        var change = root.GetProperty("kind").GetString() switch
        {
            "set" => SecretChange.Set(new SecretDraft("synthetic-installed-value")),
            "keep" => SecretChange.Keep(), "clear" => SecretChange.Clear(),
            _ => throw new IOException("fixture_change_invalid")
        };
        DocumentCommit result;
        try
        {
            result = await context.SecretDocuments!.WriteAsync(call.OperationId!.Value, "tasks", root.GetProperty("revision").GetInt64(),
                JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("document")),
                [new(root.GetProperty("subjectId").GetGuid(), "credential", root.GetProperty("secretRevision").GetInt64(), change)], cancellation);
        }
        finally
        {
            try { change.UseValue(_ => { }); }
            catch (ObjectDisposedException) { File.WriteAllText(Path.Combine(directory, "secret-draft-retired"), "disposed"); }
        }
        return Json(result);
    }

    private static FrontendResult Json(object? value) => new("application/json", JsonSerializer.SerializeToUtf8Bytes(value));
}
