using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.Configuration.Scripting;

internal sealed record TaskConfigResource(string Id, string Format);

/// <summary>Owner-local revisions; opaque tokens never expose configuration hashes.</summary>
internal sealed class TaskConfigView
{
    private sealed record Entry(string Path, byte[] Bytes, string Revision, string Format, bool Writable);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    internal TaskConfigResource[] ConfigResources => _entries.Where(p => p.Value.Writable).Select(p => new TaskConfigResource(p.Key, p.Value.Format)).ToArray();

    internal void AddConfig(string id, string path, string format) => Add(id, path, format, true);
    internal void AddResource(string id, string path, string format) => Add(id, path, format, false);

    private void Add(string id, string path, string format, bool writable)
    {
        if (_entries.Count >= 256 || _entries.ContainsKey(id)) throw new InvalidDataException("resource_limit: duplicate/excess resource");
        ValidatePath(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 2 * 1024 * 1024) throw new InvalidDataException("config_unavailable: missing/oversize resource");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("resource_limit: resource changed while reading");
        if (_entries.Values.Sum(e => (long)e.Bytes.Length) + bytes.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("resource_limit: aggregate configuration exceeds 32 MiB");
        _entries.Add(id, new(Path.GetFullPath(path), bytes, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), format, writable));
    }

    internal static void ValidatePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("config_unavailable: reparse resource");
    }

    internal string ReadConfig(string id)
    {
        if (!_entries.TryGetValue(id, out var entry) || !entry.Writable) throw new InvalidDataException("config_unavailable: undeclared config resource");
        return Read(entry);
    }

    internal string ReadResource(string id)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.Writable) throw new InvalidDataException("config_unavailable: undeclared read resource");
        return Read(entry);
    }

    private static string Read(Entry entry)
    {
        JsonNode? document = entry.Format == "text"
            ? JsonValue.Create(new System.Text.UTF8Encoding(false, true).GetString(entry.Bytes))
            : new TaskConfigDocument(entry.Bytes, entry.Format).Document;
        return new JsonObject { ["document"] = document, ["revision"] = entry.Revision, ["format"] = entry.Format }.ToJsonString();
    }

    internal byte[] Stage(string id, string revision, IReadOnlyList<TaskConfigOperation> operations, IReadOnlySet<string> allowed)
    {
        if (!_entries.TryGetValue(id, out var entry) || !entry.Writable || entry.Revision != revision)
            throw new InvalidDataException("configuration_conflict: revision");
        VerifyUnchanged(entry);
        return new TaskConfigDocument(entry.Bytes, entry.Format).Patch(operations, allowed);
    }

    internal void VerifyUnchanged()
    {
        foreach (var entry in _entries.Values) VerifyUnchanged(entry);
    }

    internal (string Path, byte[] Bytes, string Format) Snapshot(string id)
    {
        if (!_entries.TryGetValue(id, out var entry) || !entry.Writable) throw new InvalidDataException("undeclared writable resource");
        VerifyUnchanged(entry);
        return (entry.Path, entry.Bytes.ToArray(), entry.Format);
    }

    private static void VerifyUnchanged(Entry entry)
    {
        ValidatePath(entry.Path);
        if (new FileInfo(entry.Path).Length != entry.Bytes.Length || !File.ReadAllBytes(entry.Path).AsSpan().SequenceEqual(entry.Bytes))
            throw new InvalidDataException("configuration_conflict: resource changed after snapshot");
    }
}
