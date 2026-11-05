using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.WindowsTests;

internal sealed record TargetRecoverySeed(ProcessIdentity Creator, ProcessIdentity? Root, string JobName, int SessionId, uint Flags,
    WorkerJobAccounting Accounting);

internal sealed class TargetRecoveryFixture : IDisposable
{
    private readonly Process _owner;
    private readonly Task<string> _stdout, _stderr;
    private SafeKernelHandle? _root;
    internal string Directory { get; }
    internal string Executable { get; }
    internal TargetRecoverySeed Seed { get; private set; } = null!;

    internal TargetRecoveryFixture(string mode)
    {
        Directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "target-recovery-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "target-recovery-native", mode, Path.Combine(Directory, "seed.json"), Path.Combine(Directory, "root.json") })
            start.ArgumentList.Add(argument);
        _owner = Process.Start(start) ?? throw new IOException("target_native_fixture_owner_missing");
        _stdout = _owner.StandardOutput.ReadToEndAsync(); _stderr = _owner.StandardError.ReadToEndAsync();
        _ = _owner.Handle;
    }

    internal async Task Ready()
    {
        var report = Path.Combine(Directory, "seed.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(report + ".ready"))
        {
            if (_owner.HasExited)
            {
                var error = await _stderr.WaitAsync(TimeSpan.FromSeconds(3));
                File.WriteAllText(Path.Combine(Directory, "owner.stderr.log"), error);
                throw new IOException("target_native_fixture_owner_exited:" + _owner.ExitCode + ":" + error);
            }
            await Task.Delay(10, deadline.Token);
        }
        Seed = JsonSerializer.Deserialize<TargetRecoverySeed>(File.ReadAllBytes(report), JsonSerializerOptions.Web)!;
        Assert.Equal(_owner.Id, Seed.Creator.ProcessId);
        Assert.Equal(Seed.Creator, ProcessIdentityReader.Read(_owner.Id));
        if (Seed.Root is { } root)
        {
            _root = JobNative.OpenProcess(0x101001, false, root.ProcessId);
            Assert.False(_root.IsInvalid);
            Assert.Equal(root, ProcessIdentityReader.Read(_root, root.ProcessId));
        }
    }

    internal async Task StopOwner()
    {
        _owner.Kill(); await _owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (WriterStopEvidence.ObserveProcess(Seed.Creator) == NexusPipeline.Core.Execution.WriterState.Running)
            await Task.Delay(10, deadline.Token);
        Assert.Equal(NexusPipeline.Core.Execution.WriterState.Stopped, WriterStopEvidence.ObserveProcess(Seed.Creator));
    }

    internal async Task AwaitJobMissing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using (var job = JobNative.OpenJobObjectW(4, false, Seed.JobName))
            {
                if (job.IsInvalid)
                {
                    Assert.Equal(2, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
                    return;
                }
            }
            await Task.Delay(10, deadline.Token);
        }
    }

    internal async Task WaitRootStopped()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (JobNative.WaitForSingleObject(_root!, 0) == 258) await Task.Delay(10, timeout.Token);
        Assert.Equal(0u, JobNative.WaitForSingleObject(_root!, 0));
    }

    internal PcTargetImage Image(string? path = null)
    {
        path ??= Seed.Root?.ImagePath ?? throw new IOException("target_native_fixture_root_missing");
        using var image = File.OpenRead(path);
        return new(path, image.Length, Convert.ToHexStringLower(SHA256.HashData(image)), PhysicalObjectIdentity.Read(image.SafeFileHandle));
    }

    public void Dispose()
    {
        try
        {
            if (!_owner.HasExited) { _owner.Kill(); Assert.True(_owner.WaitForExit(10000)); }
            if (_root is not null && !_root.IsInvalid && JobNative.WaitForSingleObject(_root, 0) == 258)
            {
                var terminated = JobNative.TerminateProcess(_root, 1);
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                // Kill-on-close may already be terminating this exact process before its handle is signaled.
                Assert.Equal(0u, JobNative.WaitForSingleObject(_root, 10000));
                Assert.True(terminated || error == 5, "target_native_fixture_termination_failed:" + error);
            }
        }
        finally
        {
            _root?.Dispose();
            if (_owner.HasExited)
            {
                File.WriteAllText(Path.Combine(Directory, "owner.stdout.log"), _stdout.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
                File.WriteAllText(Path.Combine(Directory, "owner.stderr.log"), _stderr.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            }
            _owner.Dispose();
        }
    }
}
