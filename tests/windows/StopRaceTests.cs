using System.Collections.Concurrent;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class StopRaceTests
{
    [Fact(DisplayName = "Natural Root Exit Raced With Native Job Stop Retains Actual Counts And Never Claims An Unproven Stop")]
    public async Task PreservesRacedStops()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "stop-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var failures = new List<string>();
        for (var index = 0; index < 32; index++)
        {
            using var job = new OwnedWorkerJob();
            using var output = new OwnedProcessOutput();
            var observed = new ConcurrentDictionary<ProcessIdentity, byte>();
            using var observer = job.ObserveProcessStarts(pid => observed.TryAdd(ProcessIdentityReader.Read(pid), 0));
            var marker = Path.Combine(directory, index.ToString("D2") + ".ready");
            var root = job.Start(fixture, ["process-output", "release-exit", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
            var stdout = output.StandardOutput.CopyToAsync(Stream.Null);
            var stderr = output.StandardError.CopyToAsync(Stream.Null);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(marker)) await Task.Delay(1, deadline.Token);
                Assert.True(observer.Drain(TimeSpan.FromSeconds(5)));
                File.WriteAllText(marker + ".release", "release");
                if (index % 3 != 0) await Task.Delay(index % 3 == 1 ? 1 : 10);
                var stopped = await job.StopAsync(TimeSpan.FromSeconds(5));
                var accounting = job.GetAccounting();
                Assert.True(observer.Drain(TimeSpan.FromSeconds(5)));
                if (!stopped) failures.Add(JsonSerializer.Serialize(new { index, root, accounting, job.LastStopObservation, observed = observed.Keys }));
                Assert.Equal((uint)observed.Count, accounting.TotalProcesses);
                Assert.Empty(observer.Failures);
                Assert.Contains(observed.Keys, value => value.ProcessId == root.ProcessId && value.CreatedAtUtc == root.CreatedAtUtc);
                Assert.All(observed.Keys, value => Assert.Equal(NexusPipeline.Core.Execution.WriterState.Stopped, WriterStopEvidence.ObserveProcess(value)));
            }
            finally { if (!File.Exists(marker + ".release")) File.WriteAllText(marker + ".release", "release"); }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
