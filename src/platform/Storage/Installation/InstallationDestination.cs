using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Installation;

internal enum InstallationDestinationKind { New, Owned, Retained }
internal sealed record InstallationDestination(string Root, InstallationDestinationKind Kind, InstallationIdentity? Identity)
{
    [SupportedOSPlatform("windows")]
    internal static InstallationDestination Inspect(string requestedRoot, InstallerPaths paths, IEnumerable<string> protectedRoots)
    {
        var root = Path.TrimEndingDirectorySeparator(PhysicalPathPolicy.NormalizeWithoutLinks(requestedRoot));
        if (Path.GetDirectoryName(root) is null || root.StartsWith("\\\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed || File.Exists(root))
            throw InstallationIdentityCodec.Invalid();
        foreach (var other in protectedRoots.Prepend(paths.ManagerRoot))
        {
            var reserved = Path.TrimEndingDirectorySeparator(PhysicalPathPolicy.NormalizeWithoutLinks(other));
            if (Overlaps(root, reserved)) throw InstallationIdentityCodec.Invalid();
        }
        var identity = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root);
        if (identity is null)
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw InstallationIdentityCodec.Invalid();
            return new(root, InstallationDestinationKind.New, null);
        }
        if (identity.State == "active") return new(root, InstallationDestinationKind.Owned, identity);
        if (ApplicationTree.Roots.Any(name => File.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(root, name))))
            throw InstallationIdentityCodec.Invalid();
        return new(root, InstallationDestinationKind.Retained, identity);
    }

    private static bool Overlaps(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right) ||
        left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
