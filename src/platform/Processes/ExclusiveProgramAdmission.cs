using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using NexusPipeline.Core.Maintenance;

namespace NexusPipeline.Platform.Processes;

internal static class ExclusiveProgramAdmission
{
    internal static ResourceClaim UserSessionClaim(string executable) =>
        new("upstream-program:" + Path.GetFileName(executable).ToLowerInvariant(), ResourceClaimMode.Mutation);

    [SupportedOSPlatform("windows")]
    internal static void RequireStopped(string executable, bool inUserSession = false)
    {
        executable = Path.GetFullPath(executable);
        using var current = inUserSession ? Process.GetCurrentProcess() : null;
        using var identity = inUserSession ? WindowsIdentity.GetCurrent() : null;
        var session = current?.SessionId;
        var user = identity?.User?.Value;
        if (inUserSession && user is null) throw new IOException("upstream_program_stop_unconfirmed");
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try
        {
            if (candidates.Length > 4096) throw new IOException("upstream_program_stop_unconfirmed");
            foreach (var process in candidates)
            {
                try
                {
                    if (process.HasExited || inUserSession && process.SessionId != session) continue;
                    using var handle = JobNative.OpenProcess(0x1000, false, process.Id);
                    if (handle.IsInvalid) throw new IOException("upstream_program_stop_unconfirmed");
                    var actual = ProcessIdentityReader.ReadMetadata(handle, process.Id);
                    if (inUserSession ? actual.UserSid == user : StringComparer.OrdinalIgnoreCase.Equals(actual.ImagePath, executable))
                        throw new IOException("upstream_program_already_running");
                }
                catch (Win32Exception error) { throw new IOException("upstream_program_stop_unconfirmed", error); }
                catch (InvalidOperationException error) { throw new IOException("upstream_program_stop_unconfirmed", error); }
            }
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }
}
