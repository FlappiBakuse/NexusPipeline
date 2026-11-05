using System.Diagnostics;
using System.Collections.Concurrent;
using NexusPipeline.Platform.Processes;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.WindowsTests;

public sealed class AndroidBudgetTests(ITestOutputHelper testOutput)
{
    [Fact(DisplayName = "Root Registration And Its Observer Deduplicate One Actual Kernel Identity Before Caching")]
    public async Task DeduplicatesKernelIdentity()
    {
        var input = Inputs(); var budget = new SharedBudget(4096);
        using var job = new OwnedWorkerJob(); using var output = new OwnedProcessOutput();
        var observed = new ConcurrentDictionary<int, ProcessIdentity>();
        var observer = job.ObserveProcessIdentities(identity => observed[identity.ProcessId] = identity, budget.Reserve);
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var reads = Task.WhenAll(stdout.ReadToEndAsync(), stderr.ReadToEndAsync());
        try
        {
            var root = job.Start(input.Executable, ["process-output", "release-exit", input.Marker], input.Directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
            await WaitAsync(() => File.Exists(input.Marker) && observed.ContainsKey(root.ProcessId));
            Assert.True(observer.Drain(TimeSpan.FromSeconds(5))); Assert.Empty(observer.Failures);
            File.WriteAllText(input.Marker + ".release", "release");
            Assert.Equal(37, await job.AwaitRootAsync(root, TimeSpan.FromSeconds(15)));
            await reads.WaitAsync(TimeSpan.FromSeconds(10));
            var inventory = job.CaptureTargetInventory();
            Assert.Equal(inventory.AllFamily.Length, budget.Count);
            Assert.True(budget.Reservations(root.ProcessId, root.CreatedAtUtc.ToFileTimeUtc()) >= 2);
            var identity = Assert.Single(inventory.AllFamily.Where(identity => identity.ProcessId == root.ProcessId));
            Assert.Equal(root.ProcessId, identity.ProcessId); Assert.Equal(root.CreatedAtUtc, identity.CreatedAtUtc);
            Assert.Equal((uint)budget.Count, inventory.TotalProcesses); Assert.Equal(0u, inventory.ActiveProcesses);
            testOutput.WriteLine(string.Join("\n", inventory.AllFamily.Select(member => member.ProcessId + ":" + member.ImagePath)));
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        }
        finally { await job.StopAsync(TimeSpan.FromSeconds(10)); output.CloseUnstartedEnds(); await reads.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "An Observed Child Can Exit Before Collection Without Losing Its Reserved Kernel Identity")]
    public async Task PreservesExitedIdentity()
    {
        var input = Inputs(); var budget = new SharedBudget(4096);
        using var job = new OwnedWorkerJob(); using var output = new OwnedProcessOutput();
        var observed = new ConcurrentDictionary<int, ProcessIdentity>();
        var observer = job.ObserveProcessIdentities(identity => observed[identity.ProcessId] = identity, budget.Reserve);
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var reads = Task.WhenAll(stdout.ReadToEndAsync(), stderr.ReadToEndAsync());
        try
        {
            var root = job.Start(input.Executable, ["process-output", "budget-short-child", input.Marker], input.Directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
            var childId = 0;
            await WaitAsync(() =>
            {
                try { return int.TryParse(File.ReadAllText(input.Marker + ".child"), out childId) && childId > 0 && observed.ContainsKey(childId); }
                catch (IOException) { return false; }
            });
            Assert.True(observer.Drain(TimeSpan.FromSeconds(5))); Assert.Empty(observer.Failures);
            using var child = Process.GetProcessById(childId); _ = child.SafeHandle;
            File.WriteAllText(input.Marker + ".child.release", "release");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(37, await job.AwaitRootAsync(root, TimeSpan.FromSeconds(10)));
            await reads.WaitAsync(TimeSpan.FromSeconds(10));
            var inventory = job.CaptureTargetInventory();
            Assert.Equal(inventory.AllFamily.Length, budget.Count);
            Assert.Contains(inventory.AllFamily, identity => identity.ProcessId == childId);
            Assert.Contains(inventory.AllFamily, identity => identity.ProcessId == root.ProcessId);
            Assert.Equal(budget.Count, inventory.AllFamily.DistinctBy(identity => (identity.ProcessId, identity.CreatedAtUtc)).Count());
            Assert.Equal(0u, inventory.ActiveProcesses);
            testOutput.WriteLine(string.Join("\n", inventory.AllFamily.Select(member => member.ProcessId + ":" + member.ImagePath)));
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        }
        finally { await job.StopAsync(TimeSpan.FromSeconds(10)); output.CloseUnstartedEnds(); await reads.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "A Shared Cross Job Limit Rejects The Second Suspended Root Before Its Admission Callback Or Resume")]
    public async Task BoundsSuspendedAdmission()
    {
        var input = Inputs(); var budget = new SharedBudget(4096);
        using var first = new OwnedWorkerJob(); using var second = new OwnedWorkerJob();
        var firstObserver = first.ObserveProcessIdentities(_ => { }, budget.Reserve);
        second.ObserveProcessIdentities(_ => { }, budget.Reserve);
        using var firstOutput = new OwnedProcessOutput(); using var secondOutput = new OwnedProcessOutput();
        using var firstOut = new StreamReader(firstOutput.StandardOutput); using var firstError = new StreamReader(firstOutput.StandardError);
        using var secondOut = new StreamReader(secondOutput.StandardOutput); using var secondError = new StreamReader(secondOutput.StandardError);
        var reads = Task.WhenAll(firstOut.ReadToEndAsync(), firstError.ReadToEndAsync(), secondOut.ReadToEndAsync(), secondError.ReadToEndAsync());
        try
        {
            var root = first.Start(input.Executable, ["process-output", "wait", input.Marker], input.Directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, firstOutput);
            await WaitAsync(() => File.Exists(input.Marker + ".output-ready"));
            Assert.True(firstObserver.Drain(TimeSpan.FromSeconds(5))); Assert.Empty(firstObserver.Failures);
            var original = first.CaptureTargetInventory();
            Assert.Equal(original.AllFamily.Length, budget.Count); budget.FreezeCount();
            var admitted = false;
            Assert.Throws<IOException>(() => second.Start(input.Executable, ["process-output", "tail", input.Marker + ".second"],
                input.Directory, Guid.NewGuid(), Guid.NewGuid(), _ => admitted = true, secondOutput));
            Assert.False(admitted); Assert.False(File.Exists(input.Marker + ".second")); Assert.Equal(original.AllFamily.Length, budget.Count);
            await WaitAsync(() => second.GetAccounting().ActiveProcesses == 0);
            Assert.Equal(1u, second.GetAccounting().TotalProcesses);
            Assert.Throws<IOException>(() => second.CaptureTargetInventory());
            Assert.Contains(root.ProcessId, first.ActiveProcessIds());
            Assert.Equal(original.AllFamily.ToArray(), first.CaptureTargetInventory().AllFamily.ToArray());
            Assert.True(await first.StopAsync(TimeSpan.FromSeconds(10)));
            await second.StopAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<IOException>(() => second.CaptureTargetInventory());
        }
        finally
        {
            await first.StopAsync(TimeSpan.FromSeconds(10)); await second.StopAsync(TimeSpan.FromSeconds(10));
            firstOutput.CloseUnstartedEnds(); secondOutput.CloseUnstartedEnds();
            await reads.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class SharedBudget(int maximum)
    {
        private int _maximum = maximum;
        private readonly object _gate = new();
        private readonly HashSet<(int ProcessId, long Created)> _known = [];
        private readonly Dictionary<(int ProcessId, long Created), int> _reservations = [];
        internal int Count { get { lock (_gate) return _known.Count; } }
        internal int Reservations(int processId, long created)
        { lock (_gate) return _reservations.GetValueOrDefault((processId, created)); }
        internal void FreezeCount() { lock (_gate) _maximum = _known.Count; }
        internal void Reserve(int processId, long created)
        {
            if (processId <= 0 || created <= 0) throw new IOException("fixture_identity_invalid");
            lock (_gate)
            {
                _reservations[(processId, created)] = _reservations.GetValueOrDefault((processId, created)) + 1;
                if (_known.Contains((processId, created))) return;
                if (_known.Count >= _maximum) throw new IOException("fixture_shared_identity_limit");
                _known.Add((processId, created));
            }
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(condition());
    }

    private static (string Directory, string Executable, string Marker) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("fixture_temp_root_missing");
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
        var directory = Path.Combine(root, "android-observation-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return (directory, executable, Path.Combine(directory, "ready"));
    }
}
