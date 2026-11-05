using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerRuntimeRemoval
{
    internal static readonly SnapshotBudget Budget = new(200000, 68_719_476_736);
    internal static string Runtime(string root) => Path.Combine(root, "runtime");
    internal static string Retired(string root) => Path.Combine(root, ".nexus-uninstall-data");

    internal static (FileTreeSnapshot Tree, PhysicalTreeObject[] Objects) Freeze(InstallationIdentity identity)
    {
        var root = Runtime(identity.ApplicationRoot);
        var runtime = new InstanceRoot(new()).OpenExisting(root);
        if (runtime.InstanceId != identity.RuntimeId || PhysicalObjectIdentity.Read(root) != identity.RuntimeIdentity ||
            Exists(Retired(identity.ApplicationRoot))) throw InstallationIdentityCodec.Invalid();
        var tree = new FileTreeScanner(Budget).Capture(root, default);
        PhysicalTreeIdentity.RequireSingleLinks(root, tree);
        var objects = PhysicalTreeIdentity.Capture(root, tree).ToArray();
        return (tree, objects);
    }

    internal static void Verify(InstallerDataIntent intent, bool removed = false)
    {
        var source = Runtime(intent.Root); var retired = Retired(intent.Root);
        if (removed)
        {
            if (Exists(source) || Exists(retired)) throw new IOException("installer_data_removal_pending");
            return;
        }
        if (Exists(source))
        {
            if (Exists(retired)) throw InstallationIdentityCodec.Invalid();
            RequireTree(source, intent, complete: true);
        }
        else if (Exists(retired)) RequireTree(retired, intent, complete: false);
    }

    internal static void Remove(InstallerDataIntent intent)
    {
        InstallerRuntimeGate.RequireHeld(intent.Root);
        Verify(intent);
        var identity = InstallationIdentityCodec.Unseal(intent.Installation, intent.Root);
        var source = Runtime(intent.Root); var retired = Retired(intent.Root);
        if (Exists(source))
            PhysicalObjectMove.MoveVerified(new(source, identity.RootIdentity, identity.RuntimeIdentity,
                retired, identity.RootIdentity, FileObjectKind.Directory));
        if (!Exists(retired)) return;
        var remaining = RequireTree(retired, intent, complete: false);
        var objects = intent.Objects.ToDictionary(item => item.RelativePath, item => item.Identity, StringComparer.Ordinal);
        foreach (var entry in remaining.Entries.OrderByDescending(entry => entry.RelativePath.Count(character => character == '/'))
            .ThenByDescending(entry => entry.RelativePath.Length))
        {
            var path = entry.RelativePath.Length == 0 ? retired : Path.Combine(retired, entry.RelativePath);
            var separator = entry.RelativePath.LastIndexOf('/');
            var parent = entry.RelativePath.Length == 0 ? identity.RootIdentity : objects[separator < 0 ? "" : entry.RelativePath[..separator]];
            PhysicalObjectDeletion.DeleteVerified(new(path, parent, objects[entry.RelativePath], entry.Kind, entry.SizeBytes, entry.Sha256));
        }
        Verify(intent, removed: true);
    }

    private static FileTreeSnapshot RequireTree(string root, InstallerDataIntent intent, bool complete)
    {
        var actual = new FileTreeScanner(Budget).Capture(root, default);
        if (actual.Shape != FileObjectKind.Directory) throw InstallationIdentityCodec.Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(root, actual);
        var expected = intent.Runtime.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var identities = intent.Objects.ToDictionary(item => item.RelativePath, item => item.Identity, StringComparer.Ordinal);
        if (complete && actual.Entries.Count != expected.Count || actual.Entries.Any(entry =>
            expected.GetValueOrDefault(entry.RelativePath) != entry || PhysicalObjectIdentity.Read(Path.Combine(root, entry.RelativePath)) != identities[entry.RelativePath]))
            throw new IOException("installer_data_objects_changed");
        return actual;
    }

    private static bool Exists(string path)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(path);
        return File.Exists(path) || Directory.Exists(path);
    }
}
