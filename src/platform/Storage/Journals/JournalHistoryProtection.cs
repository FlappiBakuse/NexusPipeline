using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Journals;

internal sealed class JournalHistoryProtection : IDisposable
{
    private const int MaximumProtectedRevisions = 256;
    private const long MaximumProtectedBytes = 16777216;
    private readonly string _directory;
    private readonly List<FileStream> _revisions = [];
    private readonly List<string> _names = [];
    private readonly List<JournalDocument> _documents = [];
    private readonly StableDirectoryLease _directoryLease;
    private StableDirectoryLease? _planDirectory;
    private FileStream? _plan;
    private string? _planPath, _planDigest;
    internal Guid JournalId { get; private set; }
    internal long TotalBytes { get; private set; }

    private JournalHistoryProtection(string directory)
    { _directory = directory; _directoryLease = StableDirectoryLease.Acquire(directory); }

    internal static JournalHistoryProtection? TryAcquire(string directory)
    {
        var names = Directory.EnumerateFileSystemEntries(directory).Take(MaximumProtectedRevisions + 1)
            .Order(StringComparer.Ordinal).ToArray();
        if (names.Length > MaximumProtectedRevisions) return null;
        var result = new JournalHistoryProtection(directory);
        try
        {
            foreach (var path in names)
            {
                PhysicalPathPolicy.NormalizeWithoutLinks(path);
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                result._revisions.Add(input); result._names.Add(Path.GetFileName(path));
                result.TotalBytes = checked(result.TotalBytes + input.Length);
                if (result.TotalBytes > MaximumProtectedBytes)
                { result.Dispose(); return null; }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void Bind(RuntimeInstance instance, IReadOnlyList<JournalDocument> documents)
    {
        if (documents.Count == 0 || documents.Count != _revisions.Count) throw Invalid();
        JournalId = documents[0].JournalId;
        _documents.AddRange(documents);
        var last = documents[^1]; _planPath = last.PlanPath; _planDigest = last.PlanDigest;
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, last.PlanPath));
        _planDirectory = StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!);
        _plan = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_plan.Length > StrictJson.MaximumBytes || Convert.ToHexStringLower(SHA256.HashData(_plan)) != last.PlanDigest)
            throw new IOException("journal_plan_digest_mismatch");
        RequireInventory();
    }

    internal bool CanRetain(long nextBytes) => _documents.Count < MaximumProtectedRevisions &&
        nextBytes >= 0 && nextBytes <= MaximumProtectedBytes - TotalBytes;

    internal IReadOnlyList<JournalDocument> Read()
    {
        RequireInventory();
        return _documents.ToArray();
    }

    internal void RequirePlan(JournalDocument document)
    {
        if (_plan is null || document.JournalId != JournalId || document.PlanPath != _planPath ||
            document.PlanDigest != _planDigest) throw Invalid();
    }

    internal void Include(JournalDocument document)
    {
        if (!CanRetain(document.Export().Length) || document.Revision != _documents.Count + 1) throw Invalid();
        RequirePlan(document);
        var name = document.Revision.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        var input = new FileStream(Path.Combine(_directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var expected = document.Export();
            if (input.Length != expected.Length) throw Invalid();
            var bytes = new byte[expected.Length]; input.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(expected)) throw Invalid();
            _revisions.Add(input); _names.Add(name); _documents.Add(document); TotalBytes += input.Length;
        }
        catch { input.Dispose(); throw; }
        RequireInventory();
    }

    private void RequireInventory()
    {
        // Read handles protect prior bytes; a new sibling entry still requires a fresh name inventory.
        if (!Directory.EnumerateFileSystemEntries(_directory).Take(MaximumProtectedRevisions + 1)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(_names)) throw Invalid();
    }

    public void Dispose()
    {
        _plan?.Dispose(); _planDirectory?.Dispose();
        for (var index = _revisions.Count - 1; index >= 0; index--) _revisions[index].Dispose();
        _directoryLease.Dispose();
    }
    private static IOException Invalid() => new("journal_protected_history_changed");
}
