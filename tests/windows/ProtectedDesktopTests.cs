using System.Diagnostics;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ProtectedDesktopTests
{
    [Fact(DisplayName = "Desktop Image And Ancestors Stay Protected From Suspended Launch Through Native Binding Release")]
    public async Task RetainsImageProtection()
    {
        var (directory, executable, marker) = Inputs();
        using var image = ProcessIdentityLease.HoldImage(executable);
        using var job = new OwnedWorkerJob();
        using var release = new ManualResetEventSlim();
        var registered = new TaskCompletionSource<OwnedWorkerIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = Task.Run(() => job.StartDesktop(executable, [marker, "protected-desktop"], directory,
            Guid.NewGuid(), Guid.NewGuid(), identity => { registered.TrySetResult(identity); release.Wait(); }, image));
        NativeClientBinding? binding = null;
        try
        {
            var identity = await registered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var expected = new ProcessIdentity(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath,
                identity.ImageSha256, identity.UserSid, identity.IsElevated);
            Assert.Equal(expected, ProcessIdentityReader.Read(identity.ProcessId));
            binding = new(expected, image);
            Assert.True(binding.IsLive);
            Assert.False(File.Exists(marker));
            Assert.Throws<IOException>(() => File.Delete(executable));
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
            release.Set();
            Assert.Equal(identity, await launch.WaitAsync(TimeSpan.FromSeconds(15)));
            var watch = Stopwatch.StartNew();
            while (!File.Exists(marker) && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            Assert.True(File.Exists(marker));
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
            Assert.False(binding.IsLive);
            Assert.Throws<IOException>(() => File.WriteAllBytes(executable, [1, 2, 3]));
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
            binding.Dispose();
            File.WriteAllBytes(executable, [1, 2, 3]);
            Directory.Move(directory, directory + "-moved");
        }
        finally
        {
            release.Set();
            try { await launch.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { await job.StopAsync(TimeSpan.FromSeconds(10)); binding?.Dispose(); }
        }
    }

    [Fact(DisplayName = "A Same Byte Image At Another Path Cannot Authorize A Protected Desktop Launch")]
    public async Task RejectsDifferentImage()
    {
        var (directory, executable, marker) = Inputs();
        var other = Path.Combine(directory, "other.exe");
        File.Copy(executable, other);
        using var image = ProcessIdentityLease.HoldImage(other);
        using var job = new OwnedWorkerJob();
        Assert.Throws<IOException>(() => job.StartDesktop(executable, [marker, "rejected"], directory,
            Guid.NewGuid(), Guid.NewGuid(), imageProtection: image));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(marker));
    }

    [Fact(DisplayName = "Failed Desktop Registration Leaves Protection With Its Caller And Never Executes The Suspended Image")]
    public async Task ReleasesFailedRegistration()
    {
        var (directory, executable, marker) = Inputs();
        using var image = ProcessIdentityLease.HoldImage(executable);
        using var job = new OwnedWorkerJob();
        Assert.Throws<IOException>(() => job.StartDesktop(executable, [marker, "rejected"], directory,
            Guid.NewGuid(), Guid.NewGuid(), identity =>
            {
                var wrong = new ProcessIdentity(identity.ProcessId, identity.CreatedAtUtc.AddTicks(1), identity.ImagePath,
                    identity.ImageSha256, identity.UserSid, identity.IsElevated);
                using var binding = new NativeClientBinding(wrong, image);
            }, image));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(marker));
        Assert.Throws<IOException>(() => File.Delete(executable));
        image.Dispose();
        File.WriteAllBytes(executable, [1, 2, 3]);
        Directory.Move(directory, directory + "-moved");
        using var rejected = new OwnedWorkerJob();
        Assert.Throws<ObjectDisposedException>(() => rejected.StartDesktop(Path.Combine(directory + "-moved", "desktop.exe"),
            [marker, "disposed"], directory + "-moved", Guid.NewGuid(), Guid.NewGuid(), imageProtection: image));
    }

    private static (string Directory, string Executable, string Marker) Inputs()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "protected-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "desktop.exe");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        foreach (var dependency in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!))
            if (Path.GetExtension(dependency) != ".exe") File.Copy(dependency, Path.Combine(directory, Path.GetFileName(dependency)));
        File.Copy(fixture, executable);
        return (directory, executable, Path.Combine(directory, "ready.json"));
    }
}
