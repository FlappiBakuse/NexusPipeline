using System.Text;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static class UpdateCleanup
{
    internal static void Complete(UpdateTask task, string root, Func<ProcessIdentity?, bool?> observeWorker,
        Action<string>? completedStep = null) => Clean(task, root, observeWorker, false, completedStep);

    internal static void Abort(UpdateTask task, string root, Func<ProcessIdentity?, bool?> observeWorker)
        => Clean(task, root, observeWorker, true, null);

    private static void Clean(UpdateTask task, string root, Func<ProcessIdentity?, bool?> observeWorker,
        bool abort, Action<string>? completedStep)
    {
        task.Validate();
        if (task.WorkerLaunchPending) throw new IOException("update cleanup worker launch unconfirmed");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        PayloadPathSafety.RequireLinkFree(root);
        string update = Path.Combine(root, ".nxp-update"), journal = Path.Combine(update, "task.json");
        string marker = Path.Combine(root, ".nxp-version"), backup = Path.Combine(root, ".nxp-backup", "previous");
        bool validPhase = abort ? task.Phase is UpdatePhase.Deferred or UpdatePhase.ApplyRequested or UpdatePhase.BackupPreparing
            : task.Phase is UpdatePhase.Committed or UpdatePhase.RollbackConfirmed;
        void Guard()
        {
            if (!validPhase || UpdateTask.Read(journal) != task)
                throw new InvalidDataException("update cleanup requires current transaction proof");
        }
        Guard();
        string stagingParent = Path.Combine(update, "staging"), stageName = Path.GetFileName(task.StagedDir);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(task.StagedDir)), stagingParent, StringComparison.OrdinalIgnoreCase)
            || stageName != task.Version && !System.Text.RegularExpressions.Regex.IsMatch(stageName,
                "^" + System.Text.RegularExpressions.Regex.Escape(task.Version) + @"\.g[1-9][0-9]*$"))
            throw new InvalidDataException("update cleanup staging identity");
        string suffix = stageName[task.Version.Length..];
        string package = $".nxp-update/NexusPipeline-v{task.Version}-win-x64.zip{suffix}";
        string checksum = $".nxp-update/NexusPipeline-v{task.Version}-win-x64.zip.sha256{suffix}";
        var version = UpdateApply.ReadVersionState(marker);
        if (version.State == UpdateFileState.Unsupported || version.State == UpdateFileState.Current && version.Value != task.Version
            || abort && version.State != UpdateFileState.Missing)
            throw new InvalidDataException("update cleanup marker identity");
        var stageInventory = task.StagingInventory!;
        var backupInventory = task.BackupInventory ?? new UpdateInventory([]);
        stageInventory.RequireOwned(task.StagedDir);
        backupInventory.RequireOwned(backup);
        RequireContainer(stagingParent, [task.StagedDir]);
        RequireContainer(Path.GetDirectoryName(backup)!, [backup]);
        RequireContainer(update, [journal, stagingParent, Path.Combine(root, package), Path.Combine(root, checksum)]);
        string? worker = task.WorkerIdentity?.ImageName;
        if (worker is not null)
        {
            string name = Path.GetFileNameWithoutExtension(worker);
            if (!string.Equals(Path.GetDirectoryName(worker), Path.Combine(root, ".nxp", "runtime", "workers"), StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith("update-", StringComparison.Ordinal) || !Guid.TryParseExact(name[7..], "N", out _)
                || Path.GetExtension(worker) != ".exe" || observeWorker(task.WorkerIdentity) != false)
                throw new IOException("update cleanup worker exit or identity unconfirmed");
            RequireFile(root, Path.GetRelativePath(root, worker).Replace('\\', '/'), task.WorkerSha256);
        }
        if (task.PackageSource == "download")
        {
            RequireFile(root, package, task.PackageSha256);
            RequireFile(root, checksum, task.PackageChecksumSha256);
        }
        else if (File.Exists(Path.Combine(root, package)) || File.Exists(Path.Combine(root, checksum)))
            throw new IOException("update cleanup unowned download");
        string markerHash = MarkerHash(task.Version);
        if (File.Exists(marker)) RequireFile(root, ".nxp-version", markerHash);

        void Step(string name, Action action) { Guard(); action(); completedStep?.Invoke(name); }
        Step("staging", () => { stageInventory.DeleteOwned(task.StagedDir, Guard); DeleteEmpty(task.StagedDir); });
        Step("downloads", () =>
        {
            if (task.PackageSource != "download") return;
            DeleteFile(root, package, task.PackageSha256, Guard);
            DeleteFile(root, checksum, task.PackageChecksumSha256, Guard);
        });
        Step("backup", () => { backupInventory.DeleteOwned(backup, Guard); DeleteEmpty(backup); });
        Step("worker", () => { if (worker is not null) DeleteFile(root, Path.GetRelativePath(root, worker).Replace('\\', '/'), task.WorkerSha256, Guard); });
        Step("containers", () => { DeleteEmpty(stagingParent); DeleteEmpty(Path.GetDirectoryName(backup)!); RequireContainer(update, [journal]); });
        Step("marker", () => DeleteFile(root, ".nxp-version", markerHash, Guard));
        Guard(); task.Clear(journal); completedStep?.Invoke("journal"); DeleteEmpty(update);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    private static string MarkerHash(string version)
    {
        // JsonUtil.WriteAtomic includes a UTF-8 preamble; ownership covers those bytes too.
        var encoding = new UTF8Encoding(true);
        return Hash([.. encoding.GetPreamble(), .. encoding.GetBytes(version + Environment.NewLine)]);
    }
    private static void RequireFile(string root, string relative, string? hash)
    {
        string path = UpdateInventory.Resolve(root, relative);
        if (Directory.Exists(path)) throw new IOException("update cleanup file replaced");
        if (File.Exists(path) && (hash is null || UpdateApply.ImageHash(path) != hash))
            throw new IOException("update cleanup file bytes changed");
    }
    private static void DeleteFile(string root, string relative, string? hash, Action guard)
    {
        guard(); RequireFile(root, relative, hash);
        string path = UpdateInventory.Resolve(root, relative);
        if (File.Exists(path)) VerifiedFileDeletion.Delete(path, new FileInfo(path).Length, hash!);
    }
    private static void RequireContainer(string path, string[] allowed)
    {
        PayloadPathSafety.RequireLinkFree(path);
        if (File.Exists(path)) throw new IOException("update cleanup container replaced");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(entry => !allowed.Contains(entry, StringComparer.OrdinalIgnoreCase)))
            throw new IOException("update cleanup contains unowned files");
    }
    private static void DeleteEmpty(string path)
    {
        PayloadPathSafety.RequireLinkFree(path);
        if (File.Exists(path)) throw new IOException("update cleanup container replaced");
        if (Directory.Exists(path)) Directory.Delete(path, false);
    }
}
