using System.Text.Json;

namespace NexusPipeline.Platform.Execution.Judgement;

internal sealed record JudgementFailure(string Stage, string Effect, string Type, int HResult, long? StderrBytes);

internal sealed class JudgementDiagnostics
{
    private const int Maximum = 8;
    private static readonly string[] Stages = ["read", "prepare-write", "create", "open-write", "truncate", "write", "flush", "file-release",
        "python-start", "python-send", "python-reply", "python-exit", "python-stop", "python-decode", "python-script", "python-release", "python-drain"];
    private static readonly string[] Effects = ["read-only", "not-started", "partial-or-unknown", "completed", "failed", "cleanup"];
    private static readonly string[] Types = ["IOException", "FileNotFoundException", "DirectoryNotFoundException", "EndOfStreamException",
        "UnauthorizedAccessException", "ArgumentException", "JsonException", "InvalidOperationException", "Win32Exception",
        "ObjectDisposedException", "OperationCanceledException", "TaskCanceledException", "TimeoutException", "Exception",
        "SyntaxError", "ValueError", "TypeError", "KeyError", "NameError", "RuntimeError", "OSError", "AssertionError"];
    private readonly List<JudgementFailure> _failures = [];
    internal IReadOnlyList<JudgementFailure>? Failures => _failures.Count == 0 ? null : _failures;

    internal void Add(string stage, string effect, Exception error, long? stderrBytes = null)
    {
        var type = error.GetType().Name;
        Add(new(stage, effect, Types.Contains(type) ? type : "Exception", error.HResult, stderrBytes));
    }

    internal void AddPythonFault(string type, long stderrBytes) => Add(new("python-script", "failed", type, 0, stderrBytes));

    private void Add(JudgementFailure value)
    {
        if (_failures.Count < Maximum && !_failures.Contains(value)) _failures.Add(value);
    }

    internal static bool IsPythonType(string? type) => type is "SyntaxError" or "ValueError" or "TypeError" or "KeyError" or
        "NameError" or "RuntimeError" or "OSError" or "AssertionError" or "Exception";

    internal static void Require(IReadOnlyList<JudgementFailure>? failures)
    {
        if (failures is null) return;
        if (failures.Count is 0 or > Maximum || failures.Any(failure => failure is null ||
            !Stages.Contains(failure.Stage) || !Effects.Contains(failure.Effect) || !Types.Contains(failure.Type) ||
            failure.StderrBytes is < 0 or > 20 * 1024 * 1024 + 4096 ||
            failure.StderrBytes is not null && !failure.Stage.StartsWith("python-", StringComparison.Ordinal)))
            throw new JsonException("judgement_diagnostics_invalid");
    }
}
