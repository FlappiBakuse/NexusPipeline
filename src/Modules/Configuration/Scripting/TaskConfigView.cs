using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.Configuration.Scripting;

internal sealed record TaskConfigResource(string Id, string Format);
internal sealed record TaskDeclaredTarget(string Value, string BaseDirectory);

/// <summary>Owner-local revisions; opaque tokens never expose configuration hashes.</summary>
internal sealed class TaskConfigView
{
    private sealed class Entry
    {
        internal string Path { get; }
        internal string BaseDirectory { get; }
        internal byte[] Bytes { get; }
        internal string Revision { get; }
        internal string Format { get; }
        internal bool Writable { get; }
        internal string? Integrity { get; }
        internal Lazy<TaskConfigDocument?> Parsed { get; }
        internal Lazy<string> Serialized { get; }

        internal Entry(string path, string baseDirectory, byte[] bytes, string revision,
            string format, bool writable, string? integrity, Action parsed)
        {
            Path = path;
            BaseDirectory = baseDirectory;
            Bytes = bytes;
            Revision = revision;
            Format = format;
            Writable = writable;
            Integrity = integrity;
            Parsed = new Lazy<TaskConfigDocument?>(() =>
            {
                if (format is not ("json" or "yaml")) return null;
                parsed();
                return new TaskConfigDocument(bytes, format);
            });
            Serialized = new Lazy<string>(() =>
            {
                JsonNode? document = integrity == "mismatch" ? null : format == "text"
                    ? JsonValue.Create(new System.Text.UTF8Encoding(false, true).GetString(bytes))
                    : Parsed.Value!.Document;
                var result = new JsonObject { ["document"] = document, ["revision"] = revision, ["format"] = format };
                if (integrity is not null) result["integrity"] = integrity;
                return result.ToJsonString();
            });
        }
    }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _parseCount;
    internal int ParseCount => Volatile.Read(ref _parseCount);
    internal TaskConfigResource[] ConfigResources => _entries.Where(p => p.Value.Writable).Select(p => new TaskConfigResource(p.Key, p.Value.Format)).ToArray();
    internal IReadOnlySet<string> DeclaredResourceIds => _entries.Keys.ToHashSet(StringComparer.Ordinal);
    private static readonly byte[] RevisionKey = RandomNumberGenerator.GetBytes(32);
    // Stable only within this Host process. The keyed digest is not an exposed content hash;
    // per-resource CAS revisions remain random and local to one immutable view.
    internal string RevisionToken
    {
        get
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, RevisionKey);
            foreach (var pair in _entries.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                byte[] id = System.Text.Encoding.UTF8.GetBytes(pair.Key);
                hash.AppendData(BitConverter.GetBytes(id.Length)); hash.AppendData(id);
                hash.AppendData(BitConverter.GetBytes(pair.Value.Bytes.Length)); hash.AppendData(pair.Value.Bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    internal void AddConfig(string id, string path, string format, string? baseDirectory = null) => Add(id, path, format, true, baseDirectory, null);
    internal void AddResource(string id, string path, string format, string? baseDirectory = null, string? sha256 = null) => Add(id, path, format, false, baseDirectory, sha256);

    private void Add(string id, string path, string format, bool writable, string? baseDirectory, string? sha256)
    {
        if (_entries.Count >= 256 || _entries.ContainsKey(id)) throw new InvalidDataException("resource_limit: duplicate/excess resource");
        ValidatePath(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 2 * 1024 * 1024) throw new InvalidDataException("config_unavailable: missing/oversize resource");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("resource_limit: resource changed while reading");
        if (_entries.Values.Sum(e => (long)e.Bytes.Length) + bytes.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("resource_limit: aggregate configuration exceeds 32 MiB");
        string fullPath = Path.GetFullPath(path);
        string fullBaseDirectory = Path.GetFullPath(baseDirectory ?? Path.GetDirectoryName(fullPath) ?? fullPath);
        string? integrity = sha256 is null ? null
            : Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase) ? "verified" : "mismatch";
        _entries.Add(id, new(fullPath, fullBaseDirectory, bytes, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            format, writable, integrity, () => Interlocked.Increment(ref _parseCount)));
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

    /// <summary>
    /// Resolve one manifest-authorized selector to a scalar target without exposing
    /// the source path or document contents to the script. Selectors are evaluated
    /// against the frozen bytes captured for this view, not the live file.
    /// </summary>
    internal bool TryResolveDeclaredTarget(string id, JsonArray selector, out TaskDeclaredTarget? target, out string status, string? defaultValue = null, bool allowInteger = false)
    {
        target = null;
        status = "not_checked";
        if (!_entries.TryGetValue(id, out var entry)) return false;
        try
        {
            if (entry.Format is not ("json" or "yaml")) return false;
            var document = entry.Parsed.Value!;
            bool useDefault = defaultValue is not null && selector.Count == 1
                && selector[0] is JsonValue property && property.TryGetValue<string>(out string? name)
                && !document.ContainsTopLevelProperty(name);
            JsonNode? selected = useDefault ? JsonValue.Create(defaultValue) : document.ReadSelection(selector);
            string? text = null;
            if (selected is JsonValue scalar)
            {
                if (scalar.TryGetValue<string>(out string? value)) text = value;
                else if (allowInteger && scalar.TryGetValue<int>(out int integer))
                    text = integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (text is null)
            {
                status = selected is null ? "missing" : "unsupported";
                return false;
            }
            string normalized = text.Trim();
            if (normalized.Length == 0)
            {
                status = "missing";
                return false;
            }
            target = new(normalized, entry.BaseDirectory);
            status = "present";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            status = "access_denied";
            return false;
        }
        catch (InvalidDataException)
        {
            status = "not_checked";
            return false;
        }
        catch (IOException)
        {
            status = "not_checked";
            return false;
        }
    }

    private static string Read(Entry entry) => entry.Serialized.Value;

    internal byte[] Stage(string id, string revision, IReadOnlyList<TaskConfigOperation> operations, IReadOnlySet<string> allowed)
    {
        if (!_entries.TryGetValue(id, out var entry) || !entry.Writable || entry.Revision != revision)
            throw new InvalidDataException("configuration_conflict: revision");
        VerifyUnchanged(entry);
        return new TaskConfigDocument(entry.Bytes, entry.Format).Patch(operations, allowed);
    }

    internal void VerifyPinnedResourcesUnchanged()
    {
        foreach (var entry in _entries.Values.Where(e => e.Integrity is not null))
        {
            if (entry.Integrity != "verified") throw new InvalidDataException("runtime_identity_changed: unverified resource");
            VerifyUnchanged(entry);
        }
    }

    internal void VerifyUnchanged()
    {
        foreach (var entry in _entries.Values) VerifyUnchanged(entry);
    }

    // OK launchers update these two operational fields during their own run.
    // Writable configuration is restored by the existing transaction; upstream
    // may update it during execution. Freeze every runtime resource instead,
    // and compare app metadata so version/update-state drift still aborts.
    internal void VerifyRestrictedOkRuntimeUnchanged()
    {
        foreach (var (id, entry) in _entries)
        {
            if (entry.Writable) continue;
            if (id != "runtime-app" || entry.Format != "json")
            {
                VerifyUnchanged(entry);
                continue;
            }
            ValidatePath(entry.Path);
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("runtime_identity_changed: app metadata unavailable");
            byte[] current = File.ReadAllBytes(entry.Path);
            if (!StableRuntimeAppFields(entry.Bytes).SequenceEqual(StableRuntimeAppFields(current)))
                throw new InvalidDataException("runtime_identity_changed: app metadata changed");
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> StableRuntimeAppFields(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("runtime_identity_changed: invalid app metadata");
            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            bool hasRunning = false;
            bool hasLastStart = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("running"))
                {
                    if (hasRunning || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("runtime_identity_changed: invalid running state");
                    hasRunning = true;
                    continue;
                }
                if (property.NameEquals("last_start"))
                {
                    if (hasLastStart)
                        throw new InvalidDataException("runtime_identity_changed: duplicate app metadata");
                    hasLastStart = true;
                    continue;
                }
                if (!fields.TryAdd(property.Name, property.Value.GetRawText()))
                    throw new InvalidDataException("runtime_identity_changed: duplicate app metadata");
            }
            if (!hasRunning) throw new InvalidDataException("runtime_identity_changed: missing running state");
            return fields;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("runtime_identity_changed: malformed app metadata", ex);
        }
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
