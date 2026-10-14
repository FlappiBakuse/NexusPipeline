namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateStoragePlan(long ComponentBytes, long FullPackageBytes, long TargetBytes,
    long BackupBytes, long WorkerBytes, long MigrationBytes, long PeakAdditionalBytes)
{
    private const long ReserveBytes = 64L * 1024 * 1024;

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
        var source = UpdateInventory.Capture(installation, "NexusPipeline.exe", "README.md", "resources/desktop", ApplicationPayload.ManifestPath);
        long backup = source.Entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.SizeBytes);
        long worker = source.Entries.SingleOrDefault(entry => entry.Path == "NexusPipeline.exe")?.SizeBytes ?? 0;
        return Create(components, full, target, backup, worker);
    }

    internal void RequireAvailable(long freeBytes)
    {
        if (freeBytes < PeakAdditionalBytes) throw new IOException("update.insufficient_space");
    }

    internal void RequireAvailable(string installation)
    {
        string volume = Path.GetPathRoot(Path.GetFullPath(installation)) ?? throw new IOException("update.storage_volume_unknown");
        RequireAvailable(new DriveInfo(volume).AvailableFreeSpace);
    }
}
