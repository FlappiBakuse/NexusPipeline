using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Records;

public sealed record RunDiagnostic(DateTimeOffset RecordedAt, string StepId, Guid? UserId,
    string Phase, string ReasonCode, string ExceptionType);

internal sealed class RunDiagnosticStore(RuntimeInstance instance, StateDatabase database)
{
    private sealed record Document(string Format, int Version, Guid JournalId, RunDiagnostic Diagnostic);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal void Record(Guid journal, string step, Guid? user, string phase, string reason, Exception error)
    {
        if (journal == Guid.Empty || step.Length is < 1 or > 120 || reason.Length is < 1 or > 120 || phase.Length is < 1 or > 40)
            throw new ArgumentException("run_diagnostic_invalid");
        lock (database)
        {
            var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "diagnostics", "runs", journal.ToString("D")));
            Directory.CreateDirectory(directory);
            PhysicalPathPolicy.NormalizeWithoutLinks(directory);
            if (Directory.EnumerateFiles(directory, "*.json").Take(128).Count() == 128) return;
            using var output = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("D") + ".json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(output, new Document("nexus.run.diagnostic", 1, journal,
                new(DateTimeOffset.UtcNow, step, user, phase, reason, error.GetType().Name)), Json);
            output.Flush(flushToDisk: true);
        }
    }

    internal IReadOnlyList<RunDiagnostic> Read(IEnumerable<Guid> journals)
    {
        var result = new List<RunDiagnostic>();
        lock (database)
            foreach (var journal in journals)
            {
                var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "diagnostics", "runs", journal.ToString("D")));
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(128))
                {
                    using var input = new FileStream(PhysicalPathPolicy.NormalizeWithoutLinks(path), FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > 4096) throw new IOException("run_diagnostic_size_invalid");
                    var document = JsonSerializer.Deserialize<Document>(input, Json) ?? throw new IOException("run_diagnostic_invalid");
                    if (document.Format != "nexus.run.diagnostic" || document.Version != 1 || document.JournalId != journal ||
                        document.Diagnostic is not { StepId.Length: >= 1 and <= 120, Phase.Length: >= 1 and <= 40,
                            ReasonCode.Length: >= 1 and <= 120, ExceptionType.Length: >= 1 and <= 160 } diagnostic ||
                        diagnostic.UserId == Guid.Empty || diagnostic.RecordedAt.Offset != TimeSpan.Zero)
                        throw new IOException("run_diagnostic_invalid");
                    result.Add(diagnostic);
                }
            }
        return result.OrderBy(item => item.RecordedAt).TakeLast(128).ToArray();
    }
}
