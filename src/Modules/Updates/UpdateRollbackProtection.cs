using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdatePreservedFile(string Area, UpdateOwnedEntry File);
internal sealed record UpdatePreservation(IReadOnlyList<UpdatePreservedFile> Files)
{
    public bool Equals(UpdatePreservation? other) => other is not null && Files.SequenceEqual(other.Files);
    public override int GetHashCode()
    {
        var hash = new HashCode(); foreach (var file in Files) hash.Add(file); return hash.ToHashCode();
    }
    internal void Validate()
    {
        if (Files is null || Files.Count is < 1 or > 8192) throw new InvalidDataException("update.preservation_limit");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Files)
            if (item is null || item.Area is not ("installation" or "staging") || item.File is null || item.File.IsDirectory
                || !ApplicationPayload.SafePath(item.File.Path) || !UpdateInventory.Hash(item.File.Sha256)
                || item.File.SizeBytes is < 0 or > 256L * 1024 * 1024 || !names.Add(item.Area + '/' + item.File.Path))
                throw new InvalidDataException("update.preservation_entry");
    }
}

internal static class UpdateRollbackProtection
{
    internal static UpdateTask Prepare(UpdateTask task, string installation, string journal, string[] assets)
    {
        var planned = task.Preservation?.Files.ToList() ?? [];
        void Inspect(string area, UpdateInventory actual, IEnumerable<UpdateOwnedEntry> allowed)
        {
            var expected = allowed.ToArray();
            foreach (var file in actual.Entries)
            {
                var alternatives = expected.Where(entry => entry.Path == file.Path && entry.IsDirectory == file.IsDirectory).ToArray();
                if (alternatives.Length == 0) throw new IOException("update.rollback_unowned_content: " + area + '/' + file.Path);
                if (alternatives.Contains(file)) continue;
                var entry = new UpdatePreservedFile(area, file);
                var previous = planned.Find(item => item.Area == area && item.File.Path == file.Path);
                if (previous is not null && previous != entry) throw new IOException("update.preserved_source_changed");
                if (previous is null) planned.Add(entry);
            }
        }
        Inspect("staging", UpdateInventory.Current(task.StagedDir, null), task.StagingInventory!.Entries);
        foreach (string asset in assets)
            Inspect("installation", UpdateInventory.Current(installation, asset),
                task.StagingInventory.Entries.Concat(task.BackupInventory!.Entries));
        if (planned.Count == 0) return task;
        var preservation = new UpdatePreservation(planned.ToArray()); preservation.Validate();
        task = task with { Preservation = preservation }; task.Write(journal);
        string preserved = Path.Combine(installation, ".nxp", "state", "updates", task.TransactionId! + ".preserved");
        PayloadPathSafety.RequireLinkFree(preserved); Directory.CreateDirectory(preserved);
        string receipt = preserved + ".json";
        PayloadPathSafety.RequireLinkFree(receipt);
        if (File.Exists(receipt))
        {
            if (new FileInfo(receipt).Length > 2 * 1024 * 1024) throw new IOException("update.preservation_receipt_conflict");
            using var existing = JsonDocument.Parse(File.ReadAllText(receipt));
            if (existing.RootElement.GetProperty("TransactionId").GetString() != task.TransactionId)
                throw new IOException("update.preservation_receipt_conflict");
            var old = existing.RootElement.GetProperty("Preservation").Deserialize<UpdatePreservation>();
            if (old is null || old.Files.Any(entry => !planned.Contains(entry))) throw new IOException("update.preservation_receipt_conflict");
            old.Validate();
            var encoding = new System.Text.UTF8Encoding(true);
            byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes(JsonSerializer.Serialize(
                new { task.TransactionId, Preservation = old }, JsonOpts.Indented))];
            if (!File.ReadAllBytes(receipt).AsSpan().SequenceEqual(expected)) throw new IOException("update.preservation_receipt_conflict");
        }
        JsonUtil.WriteAtomic(receipt, JsonSerializer.Serialize(new { task.TransactionId, Preservation = preservation }, JsonOpts.Indented));
        foreach (var item in planned)
        {
            Guard(task, journal);
            string source = UpdateInventory.Resolve(item.Area == "staging" ? task.StagedDir : installation, item.File.Path);
            string destination = UpdateInventory.Resolve(preserved, item.Area + '/' + item.File.Path);
            if (File.Exists(destination))
            {
                RequireFile(destination, item.File);
                if (File.Exists(source) || Directory.Exists(source)) throw new IOException("update.preservation_destination_conflict");
                continue;
            }
            RequireFile(source, item.File);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, false);
            RequireFile(destination, item.File);
        }
        return task;
    }

    internal static UpdateInventory CurrentOwned(UpdateTask task, string installation, string asset)
    {
        var actual = UpdateInventory.Current(installation, asset);
        var allowed = task.StagingInventory!.Entries.Concat(task.BackupInventory!.Entries).ToArray();
        if (actual.Entries.Any(entry => !allowed.Contains(entry))) throw new IOException("update.rollback_content_changed");
        return actual;
    }
    internal static void Guard(UpdateTask task, string journal)
    {
        if (UpdateTask.Read(journal) != task) throw new InvalidDataException("update.rollback_journal_changed");
    }
    private static void RequireFile(string path, UpdateOwnedEntry entry)
    {
        PayloadPathSafety.RequireLinkFree(path);
        if (Directory.Exists(path) || !File.Exists(path) || new FileInfo(path).Length != entry.SizeBytes || UpdateApply.ImageHash(path) != entry.Sha256)
            throw new IOException("update.preserved_bytes_changed");
    }
}
