using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Configuration.Exchange;

internal sealed record TaskConfigPatch(string ResourceId, string Format, string ExpectedRevision, TaskConfigOperation[] Operations);

/// <summary>Selection restoration and multi-file journal, executed only under the existing configuration lease.</summary>
internal sealed class TaskSelectionTransaction
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 64 };
    private readonly string _directory;
    private readonly Journal _journal;
    private readonly Action<string>? _fault;

    internal sealed class Selection
    {
        public string ResourceId { get; set; } = "";
        public string Path { get; set; } = "";
        public string Format { get; set; } = "";
        public JsonArray Selector { get; set; } = new();
        public string Purpose { get; set; } = "";
        public JsonNode? Original { get; set; }
        public JsonNode? Current { get; set; }
    }
    internal sealed class StagedFile
    {
        public string Path { get; set; } = "";
        public byte[] Before { get; set; } = [];
        public byte[] After { get; set; } = [];
        public TaskConfigOperation[] Operations { get; set; } = [];
    }
    internal sealed class Journal
    {
        public int SchemaVersion { get; set; } = 1;
        public string Phase { get; set; } = "frozen";
        public List<Selection> Fields { get; set; } = new();
        public List<StagedFile> Pending { get; set; } = new();
        public List<JsonNode?> NextValues { get; set; } = new();
        public ConfigSessionMark? Owner { get; set; }
    }

    private TaskSelectionTransaction(string directory, Journal journal, Action<string>? fault)
    { _directory = directory; _journal = journal; _fault = fault; }

    internal static TaskSelectionTransaction Freeze(string directory, TaskConfigView view, TaskSelectionField[] fields, Action<string>? fault = null, ConfigSessionMark? owner = null)
    {
        if (fields.Length > 2048 || fields.Select(f => f.ResourceId).Distinct(StringComparer.Ordinal).Count() > 32)
            throw new InvalidDataException("resource_limit: selection journal scope");
        if (Directory.Exists(directory)) throw new InvalidDataException("configuration_busy: unresolved task selection journal");
        var journal = new Journal { Owner = owner };
        foreach (var field in fields)
        {
            var resource = view.Snapshot(field.ResourceId);
            var document = new TaskConfigDocument(resource.Bytes, resource.Format);
            var value = document.ReadSelection(field.Selector);
            // A no-op patch validates selector safety, scalar type and exact local round-trip before storing any journal.
            document.Patch([new(field.Selector, value, value, field.Purpose)], new HashSet<string> { field.Selector.ToJsonString() });
            journal.Fields.Add(new() { ResourceId = field.ResourceId, Path = resource.Path, Format = resource.Format,
                Selector = (JsonArray)field.Selector.DeepClone(), Purpose = field.Purpose, Original = value?.DeepClone(), Current = value?.DeepClone() });
        }
        Directory.CreateDirectory(directory);
        var transaction = new TaskSelectionTransaction(directory, journal, fault);
        transaction.Save();
        fault?.Invoke("frozen");
        return transaction;
    }

    internal void Apply(TaskConfigView view, TaskConfigPatch[] patches)
    {
        if (patches is null || patches.Any(p => p is null || p.Operations is null || p.Operations.Length > 2048 || p.Operations.Any(o => o is null)))
            throw new InvalidDataException("invalid patch transaction");
        if (_journal.Pending.Count > 0 || patches.Length > 32 || patches.Select(p => p.ResourceId).Distinct(StringComparer.Ordinal).Count() != patches.Length)
            throw new InvalidDataException("invalid patch transaction");
        var next = _journal.Fields.Select(f => f.Current?.DeepClone()).ToList();
        var staged = new List<StagedFile>();
        foreach (var patch in patches)
        {
            var resource = view.Snapshot(patch.ResourceId);
            if (patch.Format != resource.Format) throw new InvalidDataException("patch format mismatch");
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in patch.Operations)
            {
                int index = _journal.Fields.FindIndex(f => f.ResourceId == patch.ResourceId && JsonNode.DeepEquals(f.Selector, operation.Selector) && f.Purpose == operation.Purpose);
                if (index < 0 || !JsonNode.DeepEquals(_journal.Fields[index].Current, operation.Expected))
                    throw new InvalidDataException("configuration_conflict: unapproved/changed selection");
                allowed.Add(operation.Selector.ToJsonString()); next[index] = operation.Value?.DeepClone();
            }
            staged.Add(new() { Path = resource.Path, Before = resource.Bytes,
                Operations = patch.Operations, After = view.Stage(patch.ResourceId, patch.ExpectedRevision, patch.Operations, allowed) });
        }
        view.VerifyUnchanged();
        Commit(staged, next, "selected");
    }

    internal void Restore()
    {
        RecoverPending();
        var staged = new List<StagedFile>();
        foreach (var group in _journal.Fields.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            TaskConfigView.ValidatePath(group.Key);
            byte[] current = ReadBounded(group.Key);
            var fields = group.ToArray();
            var operations = fields.Select(f => new TaskConfigOperation(f.Selector, f.Current, f.Original, f.Purpose)).ToArray();
            byte[] after = new TaskConfigDocument(current, fields[0].Format).Patch(operations,
                fields.Select(f => f.Selector.ToJsonString()).ToHashSet(StringComparer.Ordinal));
            staged.Add(new() { Path = group.Key, Before = current, After = after, Operations = operations });
        }
        Commit(staged, _journal.Fields.Select(f => f.Original?.DeepClone()).ToList(), "restored");
    }

    private void Commit(List<StagedFile> staged, List<JsonNode?> next, string finalPhase)
    {
        _journal.Pending = staged; _journal.NextValues = next; _journal.Phase = "staged"; Save(); _fault?.Invoke("staged");
        for (int i = 0; i < staged.Count; i++)
        {
            var file = staged[i]; TaskConfigView.ValidatePath(file.Path);
            if (!ReadBounded(file.Path).AsSpan().SequenceEqual(file.Before)) throw new InvalidDataException("configuration_conflict: commit CAS");
            Atomic(file.Path, file.After); _fault?.Invoke("file:" + i);
        }
        // The durable committed marker determines recovery even if the process dies before in-memory fields advance.
        _journal.Phase = "committed"; Save(); _fault?.Invoke("committed");
        for (int i = 0; i < next.Count; i++) _journal.Fields[i].Current = next[i]?.DeepClone();
        _journal.Pending.Clear(); _journal.NextValues.Clear(); _journal.Phase = finalPhase; Save(); _fault?.Invoke(finalPhase);
    }

    private void RecoverPending()
    {
        if (_journal.Phase == "committed")
        {
            if (_journal.NextValues.Count != _journal.Fields.Count) throw new InvalidDataException("recovery_conflict: selection journal");
            for (int i = 0; i < _journal.Fields.Count; i++) _journal.Fields[i].Current = _journal.NextValues[i]?.DeepClone();
        }
        else if (_journal.Pending.Count > 0)
        {
            // Prevalidate the whole rollback before touching any file. Never overwrite third-party edits.
            foreach (var file in _journal.Pending)
            {
                byte[] current = ReadBounded(file.Path);
                if (!current.AsSpan().SequenceEqual(file.Before) && !current.AsSpan().SequenceEqual(file.After))
                    throw new InvalidDataException("recovery_conflict: external configuration edit");
            }
            foreach (var file in _journal.Pending) Atomic(file.Path, file.Before);
        }
        _journal.Pending.Clear(); _journal.NextValues.Clear(); _journal.Phase = "selected"; Save();
    }

    internal static TaskSelectionTransaction Load(string directory, IReadOnlySet<string> allowedPaths)
    {
        string path = Path.Combine(directory, "journal.json");
        TaskConfigView.ValidatePath(path);
        if (new FileInfo(path).Length > 192 * 1024 * 1024) throw new InvalidDataException("resource_limit: selection journal");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path), Options) ?? throw new InvalidDataException("selection journal missing");
        if (journal.SchemaVersion != 1 || journal.Fields is null || journal.Pending is null || journal.NextValues is null
            || journal.Fields.Count > 2048 || journal.Pending.Count > 32
            || journal.Fields.Any(f => f is null || f.Selector is null || f.Original is null || f.Current is null
                || f.Format is not ("json" or "yaml") || f.Purpose is not ("selection" or "cursor")
                || string.IsNullOrWhiteSpace(f.ResourceId) || !Path.IsPathFullyQualified(f.Path))
            || journal.Pending.Any(f => f is null || f.Before is null || f.After is null || f.Operations is null || f.Operations.Length > 2048 || f.Operations.Any(o => o is null)
                || f.Before.Length > 2 * 1024 * 1024 || f.After.Length > 2 * 1024 * 1024)
            || journal.Phase is not ("frozen" or "staged" or "committed" or "selected" or "restored"))
            throw new InvalidDataException("unsupported selection journal");
        if (journal.Fields.Select(f => (f.ResourceId, f.Selector.ToJsonString())).Distinct().Count() != journal.Fields.Count
            || journal.Pending.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Pending.Count
            || (journal.Phase is "staged" or "committed"
                ? journal.NextValues.Count != journal.Fields.Count || journal.NextValues.Any(v => v is null)
                : journal.Pending.Count != 0 || journal.NextValues.Count != 0))
            throw new InvalidDataException("recovery_conflict: selection journal structure");
        foreach (var target in journal.Fields.Select(f => f.Path).Concat(journal.Pending.Select(f => f.Path)))
        {
            if (!allowedPaths.Contains(Path.GetFullPath(target))) throw new InvalidDataException("recovery_conflict: journal target ownership");
            TaskConfigView.ValidatePath(target);
        }
        // A pending byte image must be exactly the declared selector edits, not an arbitrary file rollback.
        foreach (var file in journal.Pending)
        {
            var fields = journal.Fields.Select((field, index) => (field, index))
                .Where(item => string.Equals(item.field.Path, file.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (fields.Length == 0 || fields.Select(item => item.field.Format).Distinct().Count() != 1)
                throw new InvalidDataException("recovery_conflict: unowned pending file");
            var document = new TaskConfigDocument(file.Before, fields[0].field.Format);
            foreach (var operation in file.Operations)
            {
                var match = fields.SingleOrDefault(item => JsonNode.DeepEquals(item.field.Selector, operation.Selector));
                if (match.field is null || match.field.Purpose != operation.Purpose
                    || !JsonNode.DeepEquals(match.field.Current, operation.Expected)
                    || !JsonNode.DeepEquals(journal.NextValues[match.index], operation.Value))
                    throw new InvalidDataException("recovery_conflict: pending selector authorization");
            }
            byte[] expected = document.Patch(file.Operations, fields.Select(item => item.field.Selector.ToJsonString()).ToHashSet(StringComparer.Ordinal));
            if (!expected.AsSpan().SequenceEqual(file.After))
                throw new InvalidDataException("recovery_conflict: pending bytes exceed declared selections");
        }
        foreach (var (field, index) in journal.Fields.Select((field, index) => (field, index)))
        {
            if (journal.Phase is "staged" or "committed" && !JsonNode.DeepEquals(field.Current, journal.NextValues[index])
                && !journal.Pending.Any(file => string.Equals(file.Path, field.Path, StringComparison.OrdinalIgnoreCase)
                    && file.Operations.Any(operation => JsonNode.DeepEquals(operation.Selector, field.Selector))))
                throw new InvalidDataException("recovery_conflict: unstaged selection value");
        }
        return new(directory, journal, null);
    }

    internal static ConfigSessionMark? ReadOwner(string directory, string scriptId, string? userId)
    {
        string path = Path.Combine(directory, "journal.json");
        TaskConfigView.ValidatePath(path);
        if (new FileInfo(path).Length > 192 * 1024 * 1024) throw new InvalidDataException("resource_limit: selection journal");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path), Options);
        var owner = journal?.Owner;
        if (journal?.SchemaVersion != 1 || owner is null || owner.ScriptId != scriptId || owner.UserId != (userId ?? "")
            || !Path.IsPathFullyQualified(owner.ConfigPath) || !Path.IsPathFullyQualified(owner.LaunchExe))
            throw new InvalidDataException("recovery_conflict: task journal owner");
        return owner;
    }

    internal void Complete()
    {
        if (_journal.Phase != "restored") throw new InvalidDataException("selection restoration not complete");
        // Only the exact journal owned by this transaction is removed. Other residue remains visible.
        File.Delete(Path.Combine(_directory, "journal.json"));
        if (!Directory.EnumerateFileSystemEntries(_directory).Any()) Directory.Delete(_directory);
    }

    private void Save() => Atomic(Path.Combine(_directory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(_journal, Options));
    private static byte[] ReadBounded(string path)
    {
        TaskConfigView.ValidatePath(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("resource_limit: config");
        return File.ReadAllBytes(path);
    }
    private static void Atomic(string path, byte[] bytes)
    {
        string temporary = path + ".nxp-task-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
}
