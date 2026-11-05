using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NestedJobTests
{
    [Theory(DisplayName = "An Actual Nested Capture Helper Is Bound To Its Existing Writer Family Before It Can Run")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsNestedHelpers(bool stopParentFirst)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "nested-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var parent = new OwnedWorkerJob(); parent.ObserveProcessIdentities(_ => { });
        using var child = new OwnedWorkerJob(); child.ObserveProcessIdentities(_ => { });
        using var output = new OwnedProcessOutput(redirectInput: true);
        var instance = Guid.NewGuid(); var attempt = Guid.NewGuid();
        var window = parent.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["capture-window-fixture", Path.Combine(root, "window.json")], root, instance, attempt,
            native => File.WriteAllBytes(Path.Combine(root, "parent-native.json"), JsonSerializer.SerializeToUtf8Bytes(native)));
        try
        {
            var ready = await OwnedCrashProcess.ReadReportAsync(Path.Combine(root, "window.json"), TimeSpan.FromSeconds(15));
            Assert.Equal("windowReady", ready.GetProperty("status").GetString());
            var helperPath = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!;
            var image = ExecutionHookImages.Capture(helperPath, runtime: true); var held = new List<IDisposable>();
            try
            {
                PlanInputLease.AcquireImage(image, held);
                var helper = child.StartNested(parent, helperPath, ["--window-capture-worker"], Path.GetDirectoryName(helperPath)!,
                    instance, attempt, native => File.WriteAllBytes(Path.Combine(root, "helper-native.json"), JsonSerializer.SerializeToUtf8Bytes(native)), output);
                var helperIdentity = new ProcessIdentity(helper.ProcessId, helper.CreatedAtUtc, helper.ImagePath,
                    helper.ImageSha256, helper.UserSid, helper.IsElevated);
                Assert.Null(await child.AwaitRootAsync(helper, TimeSpan.FromMilliseconds(500)));
                var nested = child.CaptureTargetInventory(); var whole = parent.CaptureTargetInventory();
                Assert.Contains(helperIdentity, nested.LivingFamily); Assert.Contains(helperIdentity, whole.LivingFamily);
                Assert.All(nested.AllFamily, member => Assert.Contains(member, whole.AllFamily));
                File.WriteAllBytes(Path.Combine(root, "nested-membership.json"), JsonSerializer.SerializeToUtf8Bytes(new { nested, whole }));
                if (stopParentFirst) _ = await PreparedStoppedTarget.StopAsync(parent, TimeSpan.FromSeconds(5));
                var stopped = await PreparedStoppedTarget.StopAsync(child, TimeSpan.FromSeconds(5));
                stopped.Use(child, inventory =>
                {
                    Assert.Contains(helperIdentity, inventory.AllFamily);
                    Assert.Empty(inventory.LivingFamily); Assert.Equal(0u, inventory.ActiveProcesses);
                    return true;
                });
                Assert.Equal(0, await output.StandardOutput.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, await output.StandardError.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(stopParentFirst, parent.ReadExitCode(window) is not null);
                if (!stopParentFirst)
                {
                    Assert.Null(await parent.AwaitRootAsync(window, TimeSpan.FromMilliseconds(100)));
                    var remaining = parent.CaptureTargetInventory();
                    Assert.Contains(remaining.LivingFamily, member => member.ProcessId == window.ProcessId && member.CreatedAtUtc == window.CreatedAtUtc);
                    Assert.DoesNotContain(remaining.LivingFamily, member => member == helperIdentity);
                }
            }
            finally { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); }
        }
        finally
        {
            _ = await PreparedStoppedTarget.StopAsync(child, TimeSpan.FromSeconds(5));
            _ = await PreparedStoppedTarget.StopAsync(parent, TimeSpan.FromSeconds(5));
        }
    }
}
