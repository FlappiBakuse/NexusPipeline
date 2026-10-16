using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateStoragePlan(long ComponentBytes, long FullPackageBytes, long TargetBytes,
    long BackupBytes, long WorkerBytes, long MigrationBytes, long PeakAdditionalBytes)
{
    private const long ReserveBytes = 64L * 1024 * 1024;
    internal bool RequiresManagerSpace { get; init; }

    internal static UpdateStoragePlan Create(long componentBytes, long fullBytes, long targetBytes,
        long backupBytes, long workerBytes, long migrationBytes = 0)
    {
        if (new[] { componentBytes, fullBytes, targetBytes, backupBytes, workerBytes, migrationBytes }.Any(value => value < 0))
            throw new InvalidDataException("update.storage_plan_invalid");
        checked
        {
            long fixedBytes = backupBytes + workerBytes + migrationBytes + 4L * 1024 * 1024 + ReserveBytes;
            long rebuildPeak = componentBytes + targetBytes * 2 + fixedBytes;
            long fallbackPeak = componentBytes + fullBytes + targetBytes + fixedBytes;
            return new(componentBytes, fullBytes, targetBytes, backupBytes, workerBytes, migrationBytes, Math.Max(rebuildPeak, fallbackPeak));
        }
    }

    internal static UpdateStoragePlan ForInstallation(string installation, long components, long full, long target)
    {
        var source = UpdateInventory.Capture(installation, "NexusPipeline.exe", UpdateRecipe.UpdateEntry, "README.md", "resources/desktop", ApplicationPayload.ManifestPath);
        long backup = source.Entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.SizeBytes);
        long worker = source.Entries.SingleOrDefault(entry => entry.Path == "NexusPipeline.exe")?.SizeBytes ?? 0;
        return Create(components, full, target, backup, worker) with { RequiresManagerSpace = InstallationOwnership.Read(installation) is not null };
    }

    internal void RequireAvailable(long freeBytes)
    {
        if (freeBytes < PeakAdditionalBytes) throw new IOException("update.insufficient_space");
    }

    internal void RequireAvailable(string installation)
    {
        var volumes = new List<VolumeSpace> { VolumeSpaceProbe.Read(installation) };
        if (RequiresManagerSpace) volumes.Add(VolumeSpaceProbe.Read(InstallationOwnership.ManagerDirectory));
        RequireAvailable(volumes);
    }

    internal void RequireAvailable(IEnumerable<VolumeSpace> volumes)
    {
        var groups = volumes.GroupBy(volume => volume.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length == 0) throw new IOException("update.storage_volume_unknown");
        foreach (var group in groups)
        {
            if (group.Any(volume => volume.AllocationUnitBytes <= 0 || volume.AvailableBytes < 0))
                throw new InvalidDataException("update.storage_plan_invalid");
            // Charge each participating volume conservatively; independent helper/journal copies
            // must still fit when the application and per-user manager are on different volumes.
            long unit = group.Max(volume => volume.AllocationUnitBytes);
            long required = checked(PeakAdditionalBytes + (3L * UpdatePackage.MaxArchiveEntries + 64) * unit);
            if (group.Min(volume => volume.AvailableBytes) < required) throw new IOException("update.insufficient_space");
        }
    }
}
