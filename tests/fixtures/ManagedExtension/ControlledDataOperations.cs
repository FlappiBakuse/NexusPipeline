using System.Text.Json;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Networking;

namespace Controlled;

internal static class ControlledDataOperations
{
    internal static async ValueTask<FrontendResult> InvokeAsync(ExtensionActivationContext context,
        FrontendCall call, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(call.Payload); var input = document.RootElement;
        object? result = call.Operation switch
        {
            "doc-read" => await context.Documents!.ReadAsync(input.GetProperty("name").GetString()!, cancellationToken),
            "doc-write" => await context.Documents!.WriteAsync(call.OperationId!.Value,
                input.GetProperty("name").GetString()!, input.GetProperty("revision").GetInt64(),
                JsonSerializer.SerializeToUtf8Bytes(input.GetProperty("value")), cancellationToken),
            "doc-commit" => await context.Documents!.ReadCommitAsync(input.GetProperty("operationId").GetGuid(), cancellationToken),
            "http-request" => await context.Http!.SendAsync(new ExtensionHttpRequest(
                new Uri(input.GetProperty("url").GetString()!), input.GetProperty("method").GetString()!,
                ReadHeaders(input),
                input.TryGetProperty("content", out var content) ? System.Text.Encoding.UTF8.GetBytes(content.GetString()!) : [],
                "application/json", input.GetProperty("maximum").GetInt32()), cancellationToken),
            _ => throw new IOException("fixture_data_operation_unknown")
        };
        return new("application/json", JsonSerializer.SerializeToUtf8Bytes(result));
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(JsonElement input) => input.TryGetProperty("headers", out var headers) ?
        headers.EnumerateObject().ToDictionary(member => member.Name, member => member.Value.GetString()!, StringComparer.OrdinalIgnoreCase) :
        new Dictionary<string, string> { ["X-Fixture"] = "declared" };
}
