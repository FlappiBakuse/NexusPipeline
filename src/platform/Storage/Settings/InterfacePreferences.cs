using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Settings;

public sealed record InterfaceValues(string Theme, IReadOnlyList<string> DashboardCards,
    IReadOnlyList<string> PinnedSettings, IReadOnlyList<string> CollapsedSettings, string Locale);
public sealed record InterfaceSnapshot(long Revision, InterfaceValues Values, Guid OperationId);
public sealed class InterfaceConflictException() : Exception("interface_revision_conflict");

public sealed class InterfacePreferences
{
    private sealed record Saved(string Format, int Version, Guid InstanceId, long Revision, InterfaceValues Values,
        Guid OperationId, string Subject, string RequestSha256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly InterfaceValues Default = new("dark", ["game-activities", "status", "running", "history-duration"],
        [], ["remote", "proxy", "advanced", "updates", "diagnostics"], "zh-CN");
    private readonly InstanceLease _owner;
    private readonly ResourceLeaseManager _resources;
    private readonly string _path;
    private long _changeVersion;
    private long _publishedRevision;
    public long ChangeVersion => Interlocked.Read(ref _changeVersion);
    public long PublishedRevision
    {
        get { _owner.VerifyHeld(_owner.Instance); return Interlocked.Read(ref _publishedRevision); }
    }

    public InterfacePreferences(InstanceLease owner, ResourceLeaseManager resources)
    {
        owner.VerifyHeld(owner.Instance); _owner = owner; _resources = resources;
        _path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(owner.Instance.RuntimeRoot, "state", "interface.json"));
        _ = Read();
    }

    public InterfaceSnapshot Read() => _owner.PublishExecution(() => Observe(Load()));

    public InterfaceSnapshot Save(Guid operationId, string subject, long expectedRevision, InterfaceValues values)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || expectedRevision < 0)
            throw new ArgumentException("interface_request_invalid");
        Validate(values);
        values = values with { DashboardCards = Array.AsReadOnly(values.DashboardCards.ToArray()),
            PinnedSettings = Array.AsReadOnly(values.PinnedSettings.ToArray()), CollapsedSettings = Array.AsReadOnly(values.CollapsedSettings.ToArray()) };
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { expectedRevision, values }, Json)));
        var admission = _resources.TryAcquire(operationId, [new("settings:interface", ResourceClaimMode.Mutation)]);
        using var lease = admission.Lease ?? throw new InterfaceConflictException();
        return _owner.PublishExecution(() =>
        {
            var current = Load();
            if (current.OperationId == operationId)
            {
                if (current.Subject != subject || current.RequestSha256 != digest) throw new InterfaceConflictException();
                return Observe(current);
            }
            if (current.Revision != expectedRevision || expectedRevision == long.MaxValue) throw new InterfaceConflictException();
            var next = new Saved("nexus.interface.preferences", 1, _owner.Instance.InstanceId, expectedRevision + 1,
                values, operationId, subject, digest);
            var staging = PhysicalPathPolicy.NormalizeWithoutLinks(_path + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { JsonSerializer.Serialize(output, next, Json); output.Flush(flushToDisk: true); }
                PhysicalPathPolicy.NormalizeWithoutLinks(_path);
                File.Move(staging, _path, overwrite: true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            return Observe(next);
        });
    }

    private InterfaceSnapshot Observe(Saved saved)
    {
        var snapshot = Snapshot(saved);
        if (Interlocked.Exchange(ref _publishedRevision, saved.Revision) != saved.Revision)
            Interlocked.Increment(ref _changeVersion);
        return snapshot;
    }

    private Saved Load()
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(_path);
        if (!File.Exists(_path)) return new("nexus.interface.preferences", 1, _owner.Instance.InstanceId, 0, Default, Guid.Empty, "", "");
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 8192) throw new IOException("interface_preferences_invalid");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var saved = parsed.RootElement.Deserialize<Saved>(Json) ?? throw new IOException("interface_preferences_invalid");
        if (saved.Format != "nexus.interface.preferences" || saved.Version != 1 || saved.InstanceId != _owner.Instance.InstanceId ||
            saved.Revision < 1 || saved.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(saved.Subject) ||
            saved.Subject.Length > 256 || saved.RequestSha256.Length != 64 ||
            saved.RequestSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("interface_preferences_invalid");
        Validate(saved.Values); return saved;
    }

    private static InterfaceSnapshot Snapshot(Saved saved) => new(saved.Revision, saved.Values with
    {
        DashboardCards = Array.AsReadOnly(saved.Values.DashboardCards.ToArray()),
        PinnedSettings = Array.AsReadOnly(saved.Values.PinnedSettings.ToArray()),
        CollapsedSettings = Array.AsReadOnly(saved.Values.CollapsedSettings.ToArray())
    }, saved.OperationId);
    private static void Validate(InterfaceValues values)
    {
        if (values is null || values.Theme is not ("dark" or "light" or "system") || values.Locale is not ("zh-CN" or "en-US"))
            throw new ArgumentException("interface_values_invalid");
        Check(values.DashboardCards, ["game-activities", "status", "running", "history-duration", "history-counts"]);
        Check(values.PinnedSettings, ["service", "remote", "proxy", "advanced", "updates", "diagnostics"]);
        Check(values.CollapsedSettings, ["service", "remote", "proxy", "advanced", "updates", "diagnostics"]);
    }
    private static void Check(IReadOnlyList<string> values, string[] allowed)
    {
        if (values is null || values.Count > allowed.Length || values.Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Any(value => !allowed.Contains(value, StringComparer.Ordinal))) throw new ArgumentException("interface_values_invalid");
    }
}
