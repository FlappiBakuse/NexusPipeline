using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Windows;

internal static class DesktopProcessFamily
{
    internal static IReadOnlyList<DesktopProcessIdentity> Capture(DesktopProcessIdentity root, string bundle)
    {
        if (!root.IsAlive()) return [];
        var snapshot = ProcessTree.SnapshotProcesses();
        var result = new List<DesktopProcessIdentity>();
        var owned = new Dictionary<int, DesktopProcessIdentity> { [root.Pid] = root };
        foreach (int pid in ProcessTree.ProcessTreeOrder(root.Pid, snapshot))
        {
            if (result.Count >= 128) throw new IOException("desktop_family_limit");
            DesktopProcessIdentity identity;
            try { identity = DesktopProcessIdentity.Read(pid); }
            catch (ArgumentException) { continue; }
            if (pid == root.Pid)
            {
                if (identity != root) throw new IOException("desktop_root_identity_changed");
            }
            else if (!owned.TryGetValue(snapshot[pid].Ppid, out var parent) || !parent.IsAlive()
                || long.Parse(identity.StartFileTime) < long.Parse(parent.StartFileTime)
                || !identity.ExecutablePath.StartsWith(Path.GetFullPath(bundle).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
                throw new IOException("desktop_descendant_identity_unconfirmed");
            owned[pid] = identity; result.Add(identity);
        }
        if (!root.IsAlive()) throw new IOException("desktop_root_exited_during_capture");
        return result;
    }
}
