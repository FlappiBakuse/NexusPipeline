using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
internal static class ExclusiveProgramAdmission
{
    internal static void RequireStopped(string executable)
    {
        executable = Path.GetFullPath(executable);
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try
        {
            if (candidates.Length > 4096) throw new IOException("upstream_program_stop_unconfirmed");
            foreach (var process in candidates)
            {
                try
                {
                    if (process.HasExited) continue;
                    using var handle = JobNative.OpenProcess(0x1000, false, process.Id);
                    if (handle.IsInvalid) throw new IOException("upstream_program_stop_unconfirmed");
                    var actual = ProcessIdentityReader.ReadMetadata(handle, process.Id);
                    if (StringComparer.OrdinalIgnoreCase.Equals(actual.ImagePath, executable))
                        throw new IOException("upstream_program_already_running");
                }
                catch (Win32Exception error) { throw new IOException("upstream_program_stop_unconfirmed", error); }
                catch (InvalidOperationException error) { throw new IOException("upstream_program_stop_unconfirmed", error); }
            }
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }
}
