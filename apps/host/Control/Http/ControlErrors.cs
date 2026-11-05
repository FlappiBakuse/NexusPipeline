using NexusPipeline.Core.Operations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Host.Control.Http;

internal static class ControlErrors
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static IResult Result(int status, string code, Guid? operationId = null) => Results.Json(new
    {
        format = "nexus.control.error", version = 1, code,
        message = "The control request could not be completed.", correlationId = Guid.NewGuid(), operationId
    }, Options, statusCode: status);

    public static IResult Secret(Exception error, bool reading, Guid? operationId = null) => error switch
    {
        OperationConflictException => Result(409, "operation_conflict", operationId),
        System.Text.EncoderFallbackException when reading => Result(409, "secret_unavailable", operationId),
        ArgumentException => Result(400, "secret_request_invalid", operationId),
        InvalidOperationException { Message: "secret_revision_conflict" } => Result(409, "revision_conflict", operationId),
        InvalidOperationException { Message: "secret_resource_conflict" } => Result(409, "resource_conflict", operationId),
        InvalidOperationException { Message: "secret_host_unavailable" } => Result(503, "host_unavailable", operationId),
        Exception when reading && error is IOException or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException => Result(409, "secret_unavailable", operationId),
        IOException => Result(409, "secret_recovery_required", operationId),
        _ => Result(500, "control_failed", operationId)
    };
}
