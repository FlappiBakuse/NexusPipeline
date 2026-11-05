using NexusPipeline.Platform.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class InitialLogLease
{
    internal static IDisposable Acquire(RuntimeInstance instance, StateDatabase database, ContractReader contracts, RunAttemptPlan plan,
        RunAttemptPlanInitialLogsItem log, bool recognizeConfigurationScenes)
    {
        var before = FileTreeProtocol.FromWire(log.Before, Array.AsReadOnly(log.OriginalObjects.Select(item =>
            new PhysicalTreeObject(item.RelativePath, FileTreeProtocol.FromWire(item.Identity))).ToArray()));
        if (!recognizeConfigurationScenes) return TreeReadLease.Acquire(log.Path, FileTreeProtocol.FromWire(log.ParentIdentity), before);
        var excluded = new List<string>(); var held = new Holds();
        try
        {
            var blobs = new ImmutableBlobStore(instance, contracts); var codec = new ConfigurationPlanCodec(instance, contracts, blobs);
            var scanner = new FileTreeScanner(SnapshotBudget.Default); var swap = new ConfigurationSwap(scanner, new(scanner, blobs));
            var registry = new RecoveryJournalRegistry(database).ReadSnapshot(Guid.ParseExact(plan.JournalId, "D"));
            foreach (var item in plan.ConfigurationPlans)
            {
                var child = codec.ReadStructure(AttemptPlanCodec.Reference(item.Plan));
                var work = Path.Combine(Path.GetDirectoryName(child.TargetPath)!, ".nexus-" + child.JournalId.ToString("D"));
                if (!Overlaps(log.Path, child.TargetPath) && !Overlaps(log.Path, work)) continue;
                var registered = registry.Journals.SingleOrDefault(journal => journal.JournalId == child.JournalId);
                if (registered is null || !Directory.Exists(Path.Combine(instance.RuntimeRoot, "journals", child.JournalId.ToString("D"))))
                {
                    if (scanner.Capture(work, default).Shape != FileObjectKind.Missing) throw Invalid();
                    held.Items.Add(TreeReadLease.Acquire(child.TargetPath, child.ParentIdentity, child.Before));
                }
                else
                {
                    var physical = new JournalStore(instance, contracts).Read(child.JournalId);
                    if (registered.Phase == JournalPhase.Quarantined || physical.Phase == JournalPhase.Quarantined ||
                        registered.PlanDigest != item.Plan.Sha256 || registered.OperationId != child.OperationId) throw Invalid();
                    var bound = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    var observed = new JournalWriterStore(database).ObserveForCurrentExecutor(child.JournalId);
                    if (!observed.IsComplete || observed.State != WriterState.Stopped) throw Invalid();
                    if (bound.Prepared is { } prepared)
                    {
                        if (!swap.CanRestore(prepared, new(instance.InstanceId, child.JournalId, true, WriterState.Stopped))) throw Invalid();
                        held.Items.Add(StableDirectoryLease.Acquire(prepared.WorkPath));
                    }
                    else
                    {
                        if (scanner.Capture(work, default).Shape != FileObjectKind.Missing) throw Invalid();
                        held.Items.Add(TreeReadLease.Acquire(child.TargetPath, child.ParentIdentity, child.Before));
                    }
                }
                excluded.Add(child.TargetPath); excluded.Add(work);
            }
            if (excluded.Count == 0) { held.Dispose(); return TreeReadLease.Acquire(log.Path, FileTreeProtocol.FromWire(log.ParentIdentity), before); }
            var current = scanner.Capture(log.Path, default);
            var objects = PhysicalTreeIdentity.Capture(log.Path, current);
            var actual = new ArchivedFileTree(current, objects, current.Entries.Where(entry => entry.Kind == FileObjectKind.File)
                .Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)).Distinct().ToArray());
            held.Items.Add(TreeReadLease.Acquire(log.Path, FileTreeProtocol.FromWire(log.ParentIdentity), actual));
            bool Keep(string relative)
            {
                var path = relative.Length == 0 ? log.Path : Path.Combine(log.Path, relative.Replace('/', Path.DirectorySeparatorChar));
                return !excluded.Any(scope => Within(scope, path));
            }
            if (!current.Entries.Where(entry => Keep(entry.RelativePath)).SequenceEqual(before.Snapshot.Entries.Where(entry => Keep(entry.RelativePath))) ||
                !objects.Where(item => Keep(item.RelativePath)).SequenceEqual(before.OriginalObjects.Where(item => Keep(item.RelativePath)))) throw Invalid();
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private static bool Overlaps(string left, string right) => Within(left, right) || Within(right, left);
    private static bool Within(string scope, string path) => StringComparer.OrdinalIgnoreCase.Equals(scope, path) ||
        path.StartsWith(scope.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static IOException Invalid() => new("attempt_log_configuration_scene_unbound");
    private sealed class Holds : IDisposable
    {
        internal readonly List<IDisposable> Items = [];
        public void Dispose() { for (var index = Items.Count - 1; index >= 0; index--) Items[index].Dispose(); Items.Clear(); }
    }
}
