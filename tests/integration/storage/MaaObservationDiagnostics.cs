using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace NexusPipeline.StorageTests;

internal sealed class MaaObservationDiagnostics : IDisposable
{
    private readonly List<string> _errors = [];
    internal MaaObservationDiagnostics() => AppDomain.CurrentDomain.FirstChanceException += Observe;
    private void Observe(object? sender, FirstChanceExceptionEventArgs args)
    {
        var error = args.Exception;
        if (error is not JsonException && error is not IOException { Message: "task_script_timeout" } &&
            error.GetType().Namespace?.StartsWith("Jint", StringComparison.Ordinal) != true) return;
        if (error.StackTrace?.Contains("AdapterObserver", StringComparison.Ordinal) != true) return;
        lock (_errors)
            if (_errors.Count < 8) _errors.Add(error.GetType().Name + ": " + error.Message[..Math.Min(error.Message.Length, 256)]);
    }
    internal string Describe() { lock (_errors) return string.Join("\n", _errors); }
    public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= Observe;
}
