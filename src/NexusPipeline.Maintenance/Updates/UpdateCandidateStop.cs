using System.Diagnostics;
using NexusPipeline.Platform.Processes;
namespace NexusPipeline.Modules.Updates;
internal static class UpdateCandidateStop
{
    internal static bool Stop(Process process, ProcessIdentity expected, string executable)
    {
        try
        {
            if (!process.HasExited)
            {
                var current = ProcessIdentity.Capture(process);
                if (current is null || !expected.Matches(current.Value) || !string.Equals(current.Value.ImageName, executable, StringComparison.OrdinalIgnoreCase)) return false;
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(10000)) return false;
            }
            var stable = Stopwatch.StartNew();
            while (stable.Elapsed < TimeSpan.FromSeconds(1))
            {
                if (RuntimeImageActivityProbe.ObserveExecutable(executable) != "inactive") return false;
                Thread.Sleep(100);
            }
            return true;
        }
        catch { return false; }
    }
}
