using System.Diagnostics;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.Tests.Platform;

public sealed class ProcessObservationContractTests
{
    private static Process StartWriter(ProcessOwnership owner)
    {
        string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) info.ArgumentList.Add(arg);
        return SystemActions.StartOwnedProcess(info, owner)!;
    }

    [Fact]
    public void FailedNativeJobQueryKeepsLiveWriterIdentityAndCannotProveQuiescence()
    {
        using var owner = ProcessOwnership.TryCreate("controlled writer")!;
        Assert.NotNull(owner);
        using var writer = StartWriter(owner);
        try
        {
            var before = owner.Observe();
            var identity = Assert.Single(before.Identities, value => value.Pid == writer.Id);
            var failed = JobProcessIdReader.Read((_, _) => new(false, 5, 0));
            var unavailable = owner.Observe(failed, ProcessIdentity.Capture);
            Assert.Equal(ProcessObservationQuality.Unavailable, unavailable.Quality);
            Assert.Equal(5, unavailable.NativeErrorCode);
            Assert.Contains(identity, unavailable.Identities);
            Assert.False(unavailable.IsTrustworthyEmpty);
            Assert.False(writer.HasExited);
            var barrier = new StableExitWindow(TimeSpan.FromMilliseconds(100));
            Assert.False(barrier.Observe(!unavailable.IsTrustworthyEmpty, DateTime.UtcNow));
            Assert.False(barrier.Observe(!unavailable.IsTrustworthyEmpty, DateTime.UtcNow.AddSeconds(10)));
        }
        finally { if (!writer.HasExited) { writer.Kill(); Assert.True(writer.WaitForExit(5000)); } }
    }

    [Fact]
    public void IdentityAccessDeniedStaysPartialAndRetainsPriorIdentityUntilConfirmedExit()
    {
        using var owner = ProcessOwnership.TryCreate("controlled identity")!;
        Assert.NotNull(owner);
        using var writer = StartWriter(owner);
        try
        {
            var identity = Assert.Single(owner.Observe().Identities, item => item.Pid == writer.Id);
            var query = new JobPidQueryResult(true, [writer.Id], null);
            var partial = owner.Observe(query, _ => throw new System.ComponentModel.Win32Exception(5));
            Assert.Equal(ProcessObservationQuality.Partial, partial.Quality);
            Assert.Equal(writer.Id, Assert.Single(partial.UnresolvedPids));
            Assert.Contains(identity, partial.Identities);
            Assert.Empty(partial.ExitedDuringCapturePids);
            Assert.False(partial.IsTrustworthyEmpty);
            writer.Kill(); Assert.True(writer.WaitForExit(5000));
            var exited = owner.Observe(query, ProcessIdentity.Capture);
            Assert.Equal(ProcessObservationQuality.Complete, exited.Quality);
            Assert.Equal(writer.Id, Assert.Single(exited.ExitedDuringCapturePids));
            Assert.DoesNotContain(identity, exited.Identities);
            Assert.True(owner.Observe(new(true, [], null), ProcessIdentity.Capture).IsTrustworthyEmpty);
        }
        finally { if (!writer.HasExited) { writer.Kill(); Assert.True(writer.WaitForExit(5000)); } }
    }
}
