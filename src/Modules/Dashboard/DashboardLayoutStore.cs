using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Dashboard;

internal sealed class DashboardLayoutStore(string path, Action<string, string>? save = null)
{
    internal const int MaximumEntries = 512;
    internal const long MaximumRevision = 9_007_199_254_740_991;
    internal static readonly string[] CoreIds = ["core:status", "core:running", "core:history-duration"];
    private readonly Action<string, string>? _save = save;
    private bool _loaded;
    private string? _expectedHash;

    internal static bool ValidId(string? id) => id is not null && id.Length <= 136 &&
        (CoreIds.Contains(id, StringComparer.Ordinal) || id == "core:history-counts" || Regex.IsMatch(id, "^plugin:[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9-]{0,63}$"));

    internal DashboardLayout? Load()
    {
        try
        {
            byte[]? bytes = ReadBytes();
            DashboardLayout? layout = bytes is null ? null : JsonSerializer.Deserialize<DashboardLayout>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), DashboardRepresentation.Json);
            if (bytes is not null && layout is null) throw new InvalidDataException("Empty layout");
            if (layout is not null) Validate(layout);
            _expectedHash = Hash(bytes);
            _loaded = true;
            return layout;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new DashboardFailure(DashboardFailureKind.Unavailable, e);
        }
    }

    internal void EnsureUnchanged()
    {
        try
        {
            if (!_loaded || Hash(ReadBytes()) != _expectedHash) throw new InvalidDataException("Layout changed outside this instance");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new DashboardFailure(DashboardFailureKind.Unavailable, e);
        }
    }

    internal void Save(DashboardLayout layout)
    {
        Validate(layout);
        EnsureUnchanged();
        string content = JsonSerializer.Serialize(layout, DashboardRepresentation.Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_save is not null) _save(path, content);
            else JsonUtil.WriteAtomic(path, content, phase =>
            {
                if (phase == JsonWritePhase.BeforeReplace) EnsureUnchanged();
            });
            var written = ReadBytes();
            if (written is null || Encoding.UTF8.GetString(written).TrimStart('\uFEFF') != content)
                throw new IOException("Layout bytes changed during commit");
            _expectedHash = Hash(written);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new DashboardFailure(DashboardFailureKind.PersistenceFailed, e);
        }
    }

    private byte[]? ReadBytes()
    {
        if (Directory.Exists(path)) throw new InvalidDataException("Layout path is a directory");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 256 * 1024) throw new InvalidDataException("Layout is too large");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Validate(DashboardLayout layout)
    {
        if (layout.SchemaVersion != 1 || !Guid.TryParseExact(layout.LayoutId, "D", out _)
            || layout.Revision is < 1 or > MaximumRevision || layout.UpdatedAtUtc.Kind != DateTimeKind.Utc
            || layout.Entries is null || layout.Entries.Length is < 2 or > MaximumEntries
            || layout.Entries.Any(e => e is null || !ValidId(e.CardId))
            || layout.Entries.Select(e => e.CardId).Distinct(StringComparer.Ordinal).Count() != layout.Entries.Length
            || new[] { "core:running", "core:history-duration" }.Any(id => !layout.Entries.Any(e => e.CardId == id)))
            throw new InvalidDataException("Invalid dashboard layout");
    }
}
