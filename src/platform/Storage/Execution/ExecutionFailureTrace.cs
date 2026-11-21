using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionFailureTrace
{
    internal static void Record(AcceptedExecutionAdmission accepted, Exception error)
    {
        var failures = new List<object>();
        foreach (var (current, position) in Expand(error, "1").Take(8))
        {
            var message = current.Message;
            var owned = new[] { "execution_", "attempt_", "configuration_", "writer_", "journal_", "runtime_", "process_", "capture_", "resource_" };
            var code = message.Length is > 0 and <= 120 && owned.Any(prefix => message.StartsWith(prefix, StringComparison.Ordinal)) &&
                message.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                ? message : null;
            var frames = new StackTrace(current, false).GetFrames().Take(12).Select(frame => frame.GetMethod())
                .Where(method => method is not null).Select(method => (method!.DeclaringType?.FullName + "." + method.Name))
                .Select(name => name.Length <= 240 ? name : name[..240]).ToArray();
            var type = current.GetType().FullName ?? current.GetType().Name;
            var sqlite = current as SqliteException;
            failures.Add(new { position, exceptionType = type[..Math.Min(type.Length, 200)], current.HResult, code,
                sqliteErrorCode = sqlite?.SqliteErrorCode, sqliteExtendedErrorCode = sqlite?.SqliteExtendedErrorCode, frames });
        }
        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "execution.failure", accepted.Identity.OperationId,
            accepted.Receipt.JournalId, failures }));
    }

    private static IEnumerable<(Exception Error, string Position)> Expand(Exception error, string position)
    {
        yield return (error, position);
        if (error is AggregateException aggregate)
            for (var index = 0; index < aggregate.InnerExceptions.Count; index++)
                foreach (var inner in Expand(aggregate.InnerExceptions[index], $"{position}.{index + 1}")) yield return inner;
        else if (error.InnerException is { } innerError)
            foreach (var inner in Expand(innerError, $"{position}.1")) yield return inner;
    }
}
