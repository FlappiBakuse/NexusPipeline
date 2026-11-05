using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ProviderWorkspace : IDisposable
{
    private readonly List<StableDirectoryLease> _held = [];
    internal ExecutionWorkspace Grant { get; }

    internal ProviderWorkspace(RuntimeInstance instance, InstanceLease lease, ExecutionProviderSession identity)
    {
        lease.VerifyHeld(instance);
        var path = Location(instance, identity);
        if (Path.Combine(path, "outputs").Length >= 248) throw new IOException("execution_workspace_path_budget");
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(PhysicalPathPolicy.NormalizeWithoutLinks(parent));
        using var ancestor = StableDirectoryLease.Acquire(parent);
        ExclusiveDirectory.Create(path);
        try
        {
            _held.Add(StableDirectoryLease.Acquire(path));
            var input = Path.Combine(path, "inputs"); var output = Path.Combine(path, "outputs");
            ExclusiveDirectory.Create(input); _held.Add(StableDirectoryLease.Acquire(input));
            ExclusiveDirectory.Create(output); _held.Add(StableDirectoryLease.Acquire(output));
            Grant = new() { Path = path, Identity = Identity(path), InputIdentity = Identity(input), OutputIdentity = Identity(output) };
        }
        catch { Dispose(); throw; }
    }

    internal static void RequireArchived(RuntimeInstance instance, ExecutionProviderSession identity,
        ExecutionWorkspace? workspace, bool required)
    {
        if (!required) { if (workspace is not null) throw Invalid(); return; }
        if (workspace is null || workspace.Path != Location(instance, identity) || workspace.Identity is null ||
            !Valid(workspace.Identity) || !Valid(workspace.InputIdentity) || !Valid(workspace.OutputIdentity) ||
            workspace.Identity == workspace.InputIdentity || workspace.Identity == workspace.OutputIdentity || workspace.InputIdentity == workspace.OutputIdentity ||
            workspace.Identity.VolumeSerial != workspace.InputIdentity.VolumeSerial || workspace.Identity.VolumeSerial != workspace.OutputIdentity.VolumeSerial) throw Invalid();
        // Completed evidence binds the original directory identity without reopening mutable runtime paths.
    }

    private static bool Hex(string value, int length) => value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Valid(FileTreePhysicalIdentity? value) => value is not null && Hex(value.VolumeSerial, 16) && Hex(value.FileId, 32);
    private static FileTreePhysicalIdentity Identity(string path)
    {
        var value = PhysicalObjectIdentity.Read(path);
        return new() { VolumeSerial = value.VolumeSerial, FileId = value.FileId };
    }
    private static string Location(RuntimeInstance instance, ExecutionProviderSession identity) =>
        Path.Combine(instance.RuntimeRoot, "work", identity.Nonce[..32]);
    private static IOException Invalid() => new("execution_workspace_unbound");
    public void Dispose() { for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose(); _held.Clear(); }
}
