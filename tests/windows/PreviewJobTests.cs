using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class PreviewJobTests
{
    [Fact(DisplayName = "More Than The Primary Historical Limit Of Short Helpers Stay Outside Its Family")]
    public async Task IsolatesShortHelpers()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Root(), "preview-domain-burst-" + Guid.NewGuid().ToString("N"))).FullName;
        var primaryMembers = new List<ProcessIdentity>();
        using var main = new OwnedWorkerJob();
        var primaryObserver = main.ObserveProcessIdentities(identity => { lock (primaryMembers) primaryMembers.Add(identity); });
        var instance = Guid.NewGuid(); var attempt = Guid.NewGuid();
        using var primaryOutput = new OwnedProcessOutput();
        var ready = Path.Combine(directory, "primary.json");
        var primary = main.Start(Fixture(), [ready, "primary"], directory,
            instance, attempt, _ => { }, primaryOutput);
        using var domain = new OwnedPreviewJob(Name());
        try
        {
            var startup = Stopwatch.StartNew();
            while (!File.Exists(ready) && startup.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
            Assert.True(File.Exists(ready)); Assert.True(primaryObserver.Drain(TimeSpan.FromSeconds(5)));
            var initial = main.GetAccounting();
            Assert.InRange(initial.TotalProcesses, 1U, 2U);
            lock (primaryMembers) Assert.Equal((int)initial.TotalProcesses, primaryMembers.Count);
            var executable = Fixture();
            const int totalHelpers = 4100;
            const int batchSize = 128;
            for (var offset = 0; offset < totalHelpers; offset += batchSize)
            {
                using var helper = new OwnedWorkerJob(); helper.ObserveProcessIdentities(_ => { });
                try
                {
                    for (var index = offset; index < Math.Min(offset + batchSize, totalHelpers); index++)
                    {
                        using var output = new OwnedProcessOutput();
                        var root = helper.StartPreview(domain, executable, ["process-output", "probe", "-1"], directory,
                            instance, attempt, actual =>
                            {
                                var identity = Identity(actual);
                                Assert.True(domain.UseObservedMember(identity, () => helper.UseObservedMember(identity, () => true)));
                                using var process = Process.GetProcessById(actual.ProcessId);
                                Assert.False(main.ContainsProcessHandle(process.SafeHandle));
                            }, output);
                        Assert.Equal(0, await helper.AwaitRootAsync(root, TimeSpan.FromSeconds(10), default));
                        var accounting = main.GetAccounting();
                        lock (primaryMembers)
                            Assert.True(accounting.TotalProcesses == initial.TotalProcesses,
                                JsonSerializer.Serialize(new { index, initial, accounting, primaryMembers }));
                    }
                }
                finally { Assert.True(await helper.StopAsync(TimeSpan.FromSeconds(10))); }
            }
            var stopped = await domain.StopAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(stopped.TotalProcesses, 4100U, 8200U); Assert.Equal(OwnedPreviewJob.LimitFlags, stopped.Flags);
            Assert.Equal(initial.TotalProcesses, main.GetAccounting().TotalProcesses);
            Assert.Contains(primary.ProcessId, main.ActiveProcessIds());
            Assert.Equal(initial.ActiveProcesses, (uint)main.ActiveProcessIds().Count);
        }
        finally { await domain.StopAsync(TimeSpan.FromSeconds(10)); Assert.True(await main.StopAsync(TimeSpan.FromSeconds(10))); }
    }

    [Fact(DisplayName = "Stop And Failed Admission Never Resume Or Leave A Helper Outside The Domain")]
    public async Task RejectsUnadmittedHelpers()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Root(), "preview-domain-cancel-" + Guid.NewGuid().ToString("N"))).FullName;
        using var domain = new OwnedPreviewJob(Name());
        using var helper = new OwnedWorkerJob(); helper.ObserveProcessIdentities(_ => { });
        using var output = new OwnedProcessOutput();
        var marker = Path.Combine(directory, "must-not-write.json");
        OwnedWorkerIdentity? suspended = null;
        try
        {
            Assert.Throws<OperationCanceledException>(() => helper.StartPreview(domain, Fixture(), [marker, "never"], directory,
                Guid.NewGuid(), Guid.NewGuid(), actual =>
                {
                    suspended = actual;
                    Assert.True(domain.UseObservedMember(Identity(actual), () => true));
                    throw new OperationCanceledException("fixture_admission_cancelled");
                }, output));
            Assert.NotNull(suspended); Assert.False(File.Exists(marker));
            await domain.StopAsync(TimeSpan.FromSeconds(10));
            Assert.True(await helper.StopAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(Identity(suspended!)));
            using var next = new OwnedWorkerJob(); using var nextOutput = new OwnedProcessOutput();
            Assert.Throws<IOException>(() => next.StartPreview(domain, Fixture(), [marker, "never"], directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => throw new InvalidOperationException("must_not_admit"), nextOutput));
            Assert.Equal(0U, next.GetAccounting().TotalProcesses); Assert.False(File.Exists(marker));
            Assert.Throws<IOException>(() => new OwnedPreviewJob(domain.Name));
        }
        finally { await domain.StopAsync(TimeSpan.FromSeconds(10)); Assert.True(await helper.StopAsync(TimeSpan.FromSeconds(10))); }
    }

    [Fact(DisplayName = "Producer Exit Closes Its Non Inherited Domain Handle And Stops Its Real Helper")]
    public async Task StopsOrphanedHelpers()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Root(), "preview-domain-exit-" + Guid.NewGuid().ToString("N"))).FullName;
        var report = Path.Combine(directory, "report.json"); var name = Name();
        var start = new ProcessStartInfo(Fixture()) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "preview-domain-owner", report, name }) start.ArgumentList.Add(argument);
        using var owner = Process.Start(start) ?? throw new IOException("fixture_owner_start_failed");
        try
        {
            var creator = ProcessIdentityReader.Read(owner.Id); var clock = Stopwatch.StartNew();
            while (!File.Exists(report) && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20);
            using var document = JsonDocument.Parse(File.ReadAllBytes(report));
            Assert.Equal(name, document.RootElement.GetProperty("jobName").GetString());
            Assert.Equal(creator, document.RootElement.GetProperty("creator").Deserialize<ProcessIdentity>());
            var child = document.RootElement.GetProperty("child").Deserialize<ProcessIdentity>()!;
            Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(child));
            Assert.False(OwnedPreviewJob.ObserveStopped(name, owner.SessionId));
            var session = owner.SessionId; owner.Kill(); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            clock.Restart();
            while (WriterStopEvidence.ObserveProcess(child) != WriterState.Stopped && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(creator));
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(child));
            Assert.True(OwnedPreviewJob.ObserveStopped(name, session));
        }
        finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
    }

    private static ProcessIdentity Identity(OwnedWorkerIdentity root) =>
        new(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
    private static string Name() => "Local\\NexusWorker-" + Guid.NewGuid().ToString("N");
    private static string Root() => Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("fixture_root_missing");
    private static string Fixture() => Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
}
