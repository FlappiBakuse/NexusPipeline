using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal static class MaintenanceDiagnostic
{
    internal static void Record(RuntimeInstance? instance, Guid operationId, string component, string stage, Exception? error = null)
    {
        if (instance is null || operationId == Guid.Empty) return;
        if (component is not ("startup" or "helper") || stage.Length is < 1 or > 40 ||
            stage.Any(value => !(char.IsAsciiLetterOrDigit(value) || value == '-'))) throw new ArgumentException("Invalid maintenance stage");
        try
        {
            var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "diagnostics"));
            Directory.CreateDirectory(directory);
            var name = $"maintenance-{component}-{operationId:D}-{Environment.ProcessId}-{(error is null ? stage : "failed")}.json";
            using var output = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var code = error is null ? null : error.Message.Length is > 0 and <= 120 && error.Message.All(value =>
                value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? error.Message : "startup_failed";
            JsonSerializer.Serialize(output, new { instance.InstanceId, operationId, stage, code,
                errorType = error?.GetType().Name, errorCode = error?.HResult,
                calls = error is null ? null : new System.Diagnostics.StackTrace(error, false).GetFrames()
                    .Select(frame => frame.GetMethod()).Where(method => method?.DeclaringType?.Namespace?.StartsWith(
                        "NexusPipeline.", StringComparison.Ordinal) == true)
                    .Select(method => method!.DeclaringType!.FullName + "." + method.Name).Take(12).ToArray(),
                processId = Environment.ProcessId, recordedAt = DateTimeOffset.UtcNow },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
