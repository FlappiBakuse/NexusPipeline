namespace NexusPipeline.Platform.Processes;

internal sealed class JobProcessDrain : IDisposable
{
    private readonly List<SafeKernelHandle> _members = [];
    private readonly IReadOnlyList<SafeKernelHandle> _retained;
    public bool IsComplete { get; private set; } = true;

    public JobProcessDrain(SafeKernelHandle job, IReadOnlyList<int> activeIds, IReadOnlyList<SafeKernelHandle> retained)
    {
        _retained = retained;
        var retainedIds = retained.Select(JobNative.GetProcessId).ToHashSet();
        if (retainedIds.Contains(0)) IsComplete = false;
        foreach (var pid in activeIds)
        {
            if (retainedIds.Contains(checked((uint)pid))) continue;
            var process = JobNative.OpenProcess(0x101000, false, pid);
            if (process.IsInvalid || !JobNative.IsProcessInJob(process, job, out var owned) || !owned)
            {
                IsComplete = false;
                process.Dispose();
                continue;
            }
            _members.Add(process);
        }
    }

    public bool AllSignaled => IsComplete && _retained.Concat(_members).All(process => JobNative.WaitForSingleObject(process, 0) == 0);

    public void Dispose()
    {
        foreach (var process in _members) process.Dispose();
    }
}
