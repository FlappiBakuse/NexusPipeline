using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Modules.Updates;

internal static class UpdateCleanup
{
    internal static void Complete(UpdateTask task, string root, Func<ProcessIdentity?, bool?> observeWorker,
        Action<string>? completedStep = null)
        => Clean(task, root, observeWorker, false, completedStep);

    internal static void Abort(UpdateTask task, string root, Func<ProcessIdentity?, bool?> observeWorker)
        => Clean(task, root, observeWorker, true, null);

    private static void Clean(UpdateTask task, string root, Func<ProcessIdentity?, bool?> observeWorker,
        bool abort, Action<string>? completedStep)
    {
        task.Validate();
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string update = Path.Combine(root, ".nxp-update");
        string journal = Path.Combine(update, "task.json");
        string marker = Path.Combine(root, ".nxp-version");
        bool validPhase = abort ? task.Phase is UpdatePhase.Deferred or UpdatePhase.ApplyRequested or UpdatePhase.BackupPreparing
            : task.Phase is UpdatePhase.Committed or UpdatePhase.RollbackConfirmed;
        if (!validPhase || UpdateTask.Read(journal) != task)
            throw new InvalidDataException("update cleanup requires current transaction proof");
        string stagingParent = Path.Combine(update, "staging");
        string stageName = Path.GetFileName(task.StagedDir);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(task.StagedDir)), stagingParent, StringComparison.OrdinalIgnoreCase)
            || stageName != task.Version && !System.Text.RegularExpressions.Regex.IsMatch(stageName,
                "^" + System.Text.RegularExpressions.Regex.Escape(task.Version) + @"\.g[1-9][0-9]*$"))
            throw new InvalidDataException("update cleanup staging identity");
        var version = UpdateApply.ReadVersionState(marker);
        if (version.State == UpdateFileState.Unsupported || version.State == UpdateFileState.Current && version.Value != task.Version)
            throw new InvalidDataException("update cleanup marker identity");
        if (abort && version.State != UpdateFileState.Missing)
            throw new InvalidDataException("update abort cannot remove commit proof");
        string backup = Path.Combine(root, ".nxp-backup", "previous");
        if (abort && task.Phase != UpdatePhase.BackupPreparing && (File.Exists(backup) || Directory.Exists(backup)))
            throw new IOException("update abort backup ownership unconfirmed");
        CheckTree(root, task.StagedDir, ["NexusPipeline.exe", "resources", "README.md", "plugins"]);
        CheckTree(root, backup, ["NexusPipeline.exe", "resources", "README.md", ".installer-identity", ".backup-ready"]);
        string? worker = task.WorkerIdentity?.ImageName;
        if (worker is not null)
        {
            string workerName = Path.GetFileNameWithoutExtension(worker);
            const string prefix = "update-";
            if (!string.Equals(Path.GetDirectoryName(worker), Path.Combine(root, ".nxp", "runtime", "workers"), StringComparison.OrdinalIgnoreCase)
                || !workerName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(workerName[prefix.Length..], "N", out _)
                || Path.GetExtension(worker) != ".exe" || observeWorker(task.WorkerIdentity) != false)
                throw new IOException("update cleanup worker exit or identity unconfirmed");
        }
        void Step(string name, Action action)
        {
            if (UpdateTask.Read(journal) != task) throw new InvalidDataException("update cleanup journal changed");
            action();
            completedStep?.Invoke(name);
        }
        Step("staging", () => DeleteTree(task.StagedDir));
        string suffix = stageName[task.Version.Length..];
        if (suffix.Length > 0)
            Step("downloads", () =>
            {
                DeleteFile(root, Path.Combine(update, $"NexusPipeline-v{task.Version}-win-x64.zip{suffix}"));
                DeleteFile(root, Path.Combine(update, $"NexusPipeline-v{task.Version}-win-x64.zip.sha256{suffix}"));
            });
        Step("backup", () => DeleteTree(backup));
        Step("worker", () => { if (worker is not null) DeleteFile(root, worker); });
        Step("containers", () =>
        {
            DeleteEmpty(stagingParent);
            DeleteEmpty(Path.GetDirectoryName(backup)!);
            if (Directory.EnumerateFileSystemEntries(update).Any(path => !string.Equals(path, journal, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("update cleanup contains unowned files");
        });
        Step("marker", () => DeleteFile(root, marker));
        task.Clear(journal);
        completedStep?.Invoke("journal");
        DeleteEmpty(update);
    }

    private static void CheckTree(string root, string path, string[] allowed)
    {
        CheckPath(root, path);
        if (!Directory.Exists(path))
        {
            if (File.Exists(path)) throw new IOException("update cleanup directory replaced");
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (!allowed.Contains(Path.GetFileName(entry))) throw new IOException("update cleanup unowned resource");
            CheckDescendants(root, entry);
        }
    }

    private static void CheckDescendants(string root, string path)
    {
        CheckPath(root, path);
        if (Directory.Exists(path))
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) CheckDescendants(root, child);
    }

    private static void CheckPath(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("update cleanup path outside installation");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("update cleanup link rejected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private static void DeleteTree(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
    private static void DeleteFile(string root, string path)
    {
        CheckPath(root, path);
        if (Directory.Exists(path)) throw new IOException("update cleanup file replaced by directory");
        if (File.Exists(path)) File.Delete(path);
    }
    private static void DeleteEmpty(string path)
    {
        if (!Directory.Exists(path)) return;
        if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("update cleanup container is not empty");
        Directory.Delete(path);
    }
}
