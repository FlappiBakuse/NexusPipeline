namespace NexusPipeline.Modules.Configuration.Scripting;

/// <summary>Opt-in owner-local diagnostic counters; captures no paths or configuration values.</summary>
internal sealed class TaskConfigMetrics : IDisposable
{
    private static readonly AsyncLocal<TaskConfigMetrics?> Active = new();
    private readonly TaskConfigMetrics? _previous;
    private readonly long[] _counts = new long[8];
    internal static TaskConfigMetrics Begin() => new();
    private TaskConfigMetrics() { _previous = Active.Value; Active.Value = this; }
    internal static void Count(int kind, long count = 1)
    { if (Active.Value is { } scope) Interlocked.Add(ref scope._counts[kind], count); }
    internal object Snapshot() => new {
        documentsConstructed = _counts[0], jsonValidationParses = _counts[1], yamlParses = _counts[2],
        treeCloneOperations = _counts[3], frozenDiskReads = _counts[4], verificationDiskReads = _counts[5],
        bridgeSerializations = _counts[6], frozenByteCopies = _counts[7],
    };
    public void Dispose() { Active.Value = _previous; }
}
