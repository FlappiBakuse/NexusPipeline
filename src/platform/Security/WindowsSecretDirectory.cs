using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Security;

[SupportedOSPlatform("windows")]
internal static class WindowsSecretDirectory
{
    internal static void Initialize(RuntimeInstance instance)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "secrets"));
        Directory.CreateDirectory(path);
        using var held = StableDirectoryLease.Acquire(path);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("secret_directory_user_unavailable");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
