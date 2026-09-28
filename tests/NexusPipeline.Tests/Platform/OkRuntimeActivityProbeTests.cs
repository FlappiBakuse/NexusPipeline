using NexusPipeline.Platform.Processes;
using NexusPipeline.Modules.Execution.Targets;
using Xunit;
using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Tests.Execution;

namespace NexusPipeline.Tests.Platform;

public sealed class OkRuntimeActivityProbeTests
{
    [Theory]
    [InlineData("python.exe")]
    [InlineData("pythonw.exe")]
    public async Task ActualWindowsImagesDistinguishThisInstallationFromAnother(string name)
    {
        using var own = new ProviderWorkerPortTests.Fixture(keepEvidence: true);
        using var other = new ProviderWorkerPortTests.Fixture(keepEvidence: true);
        string Prepare(ProviderWorkerPortTests.Fixture fixture)
        {
            string runtime = Path.Combine(fixture.Root, "data", "apps", "ok-nte", "python");
            Directory.CreateDirectory(runtime);
            foreach (string file in Directory.EnumerateFiles(fixture.Root))
                File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)));
            File.Copy(Path.Combine(runtime, "NexusPipeline.TestProviderWorker.exe"), Path.Combine(runtime, name));
            File.WriteAllText(Path.Combine(runtime, ".nxp-test-fixture"), "owned-process-contract");
            return Path.Combine(runtime, name);
        }
        async Task<Process> Start(string image)
        {
            Process process = Process.Start(new ProcessStartInfo(image, "--owned-lifetime")
            {
                WorkingDirectory = Path.GetDirectoryName(image), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardInput = true,
            })!;
            Assert.Equal("owned-ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            return process;
        }
        string ownImage = Prepare(own), otherImage = Prepare(other);
        using Process unrelated = await Start(otherImage);
        using Process worker = await Start(ownImage);
        try
        {
            string active = OkRuntimeActivityProbe.Observe("oknte", own.Root);
            Assert.Equal("active", active);
            worker.StandardInput.WriteLine("stop");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            string idle = OkRuntimeActivityProbe.Observe("oknte", own.Root);
            Assert.Equal("inactive", idle);
            Assert.False(unrelated.HasExited);
            File.WriteAllText(Path.Combine(own.Root, "actual-runtime-image-evidence.json"), JsonSerializer.Serialize(new {
                ownImage, otherImage, active, idle, ownPid = worker.Id, ownExited = worker.HasExited,
                otherPid = unrelated.Id, otherAlive = !unrelated.HasExited,
                scope = "Controlled .NET helper images named python/pythonw; no upstream/game code executed",
            }));
        }
        finally
        {
            foreach (Process process in new[] { worker, unrelated })
                if (!process.HasExited) { process.StandardInput.WriteLine("stop"); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    [Fact]
    public void EmbeddedWorkerIdentityOverridesPersistedLauncherState()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-ok-probe-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, "data", "apps", "ok-nte", "python");
        Directory.CreateDirectory(runtime);
        string worker = Path.Combine(runtime, "python.exe");
        File.WriteAllBytes(worker, [0]);
        try
        {
            Assert.Equal("inactive", OkRuntimeActivityProbe.Observe("oknte", root,
                _ => new(true, [])));
            Assert.Equal("active", OkRuntimeActivityProbe.Observe("oknte", root,
                _ => new(true, [worker])));
            Assert.Equal("unknown", OkRuntimeActivityProbe.Observe("oknte", root,
                _ => new(false, [])));
            Assert.Equal("unknown", OkRuntimeActivityProbe.Observe("oknte", Path.Combine(root, "missing"),
                _ => new(true, [])));
            Assert.Equal("unknown", OkRuntimeActivityProbe.Observe("unrelated", root,
                _ => new(true, [])));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
