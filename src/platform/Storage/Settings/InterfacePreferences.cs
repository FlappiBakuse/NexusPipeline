using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Settings;

public sealed record InterfaceValues(string Theme, IReadOnlyList<string> DashboardCards,
    IReadOnlyList<string> PinnedSettings, IReadOnlyList<string> CollapsedSettings, string Locale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? DashboardAvailableCards = null);
public sealed record InterfaceSnapshot(long Revision, InterfaceValues Values, Guid OperationId);
public sealed class InterfaceConflictException() : Exception("interface_revision_conflict");

public sealed class InterfacePreferences
{
    internal const string FileName = "interface.json";
    private sealed record Saved(string Format, int Version, Guid InstanceId, long Revision, InterfaceValues Values,
        Guid OperationId, string Subject, string RequestSha256);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly InterfaceValues Default = new("dark", ["status", "running", "history-duration"],
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
        _path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(owner.Instance.RuntimeRoot, "state", FileName));
        _ = Read();
    }

    public InterfaceSnapshot Read() => _owner.PublishExecution(() => Observe(Load()));

    internal static string DefaultTheme => Default.Theme;

    internal static string ReadTheme(InstanceLease owner) => owner.PublishExecution(() =>
        Load(Path.Combine(owner.Instance.RuntimeRoot, "state", FileName), owner.Instance.InstanceId).Values.Theme);

    public InterfaceSnapshot Save(Guid operationId, string subject, long expectedRevision, InterfaceValues values)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || expectedRevision < 0)
            throw new ArgumentException("interface_request_invalid");
        Validate(values);
        values = values with { DashboardCards = Array.AsReadOnly(values.DashboardCards.ToArray()),
            DashboardAvailableCards = values.DashboardAvailableCards is null ? null : Array.AsReadOnly(values.DashboardAvailableCards.ToArray()),
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
            Publish(staging, _path, output => JsonSerializer.Serialize(output, next, Json));
            return Observe(next);
        });
    }

    internal static void Publish(string staging, string path, Action<FileStream> write)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        staging = PhysicalPathPolicy.NormalizeWithoutLinks(staging);
        path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        var stagingParent = Path.GetDirectoryName(staging)!;
        var targetParent = Path.GetDirectoryName(path)!;
        using var parents = StableDirectoryLease.Acquire(stagingParent);
        using var target = StringComparer.OrdinalIgnoreCase.Equals(stagingParent, targetParent) ? null : StableDirectoryLease.Acquire(targetParent);
        using var directory = PhysicalDirectoryHandle.Open(stagingParent);
        var parentIdentity = directory.ReadIdentity();
        using var owned = directory.CreateNewOwnedFile(Path.GetFileName(staging), writeThrough: true);
        using var output = new FileStream(owned, FileAccess.ReadWrite, bufferSize: 1);
        var identity = PhysicalObjectIdentity.Read(owned);
        FileObjectDeletion? deletion = null;
        try
        {
            write(output);
            output.Flush(flushToDisk: true);
            output.Position = 0;
            deletion = new(staging, parentIdentity, identity, FileObjectKind.File,
                output.Length, Convert.ToHexStringLower(SHA256.HashData(output)));
            output.Dispose();
            using var ready = OpenReadyStage(deletion);
            File.Move(staging, path, overwrite: true);
            if (PhysicalObjectIdentity.Read(path) != identity) throw new IOException("interface_stage_publication_unknown");
        }
        catch (Exception error)
        {
            var failures = new CleanupFailures();
            failures.Attempt(() =>
            {
                if (!owned.IsClosed) PhysicalObjectDeletion.DeleteNewlyCreatedFile(owned);
                else if (Path.Exists(staging))
                {
                    if (deletion is null || PhysicalObjectIdentity.Read(staging) != identity)
                        throw new IOException("interface_stage_cleanup_unknown");
                    PhysicalObjectDeletion.DeleteVerified(deletion);
                }
            });
            failures.Attempt(output.Dispose);
            failures.Preserve(error);
            throw;
        }
    }

    internal static FileStream OpenReadyStage(FileObjectDeletion stage)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(stage.Path);
        var ready = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!) != stage.ParentIdentity ||
                PhysicalObjectIdentity.Read(ready.SafeFileHandle) != stage.Identity || PhysicalObjectIdentity.Read(path) != stage.Identity ||
                ready.Length != stage.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(ready)) != stage.Sha256)
                throw new IOException("interface_stage_publication_unknown");
            ready.Position = 0;
            return ready;
        }
        catch { ready.Dispose(); throw; }
    }

    private InterfaceSnapshot Observe(Saved saved)
    {
        var snapshot = Snapshot(saved);
        if (Interlocked.Exchange(ref _publishedRevision, saved.Revision) != saved.Revision)
            Interlocked.Increment(ref _changeVersion);
        return snapshot;
    }

    private Saved Load() => Load(_path, _owner.Instance.InstanceId);

    private static Saved Load(string path, Guid instanceId)
    {
        path = PhysicalPathPolicy.NormalizeWithoutLinks(path);
        if (!File.Exists(path)) return new("nexus.interface.preferences", 1, instanceId, 0, Default, Guid.Empty, "", "");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 16384) throw new IOException("interface_preferences_invalid");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var saved = parsed.RootElement.Deserialize<Saved>(Json) ?? throw new IOException("interface_preferences_invalid");
        if (saved.Format != "nexus.interface.preferences" || saved.Version != 1 || saved.InstanceId != instanceId ||
            saved.Revision < 1 || saved.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(saved.Subject) ||
            saved.Subject.Length > 256 || saved.RequestSha256.Length != 64 ||
            saved.RequestSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("interface_preferences_invalid");
        Validate(saved.Values); return saved;
    }

    private static InterfaceSnapshot Snapshot(Saved saved) => new(saved.Revision, saved.Values with
    {
        DashboardCards = Array.AsReadOnly(saved.Values.DashboardCards.ToArray()),
        DashboardAvailableCards = saved.Values.DashboardAvailableCards is null ? null : Array.AsReadOnly(saved.Values.DashboardAvailableCards.ToArray()),
        PinnedSettings = Array.AsReadOnly(saved.Values.PinnedSettings.ToArray()),
        CollapsedSettings = Array.AsReadOnly(saved.Values.CollapsedSettings.ToArray())
    }, saved.OperationId);
    private static void Validate(InterfaceValues values)
    {
        if (values is null || values.Theme is not ("dark" or "light" or "system") || values.Locale is not ("zh-CN" or "en-US"))
            throw new ArgumentException("interface_values_invalid");
        CheckCards(values.DashboardCards);
        if (values.DashboardAvailableCards is not null) CheckCards(values.DashboardAvailableCards);
        Check(values.PinnedSettings, ["service", "remote", "proxy", "advanced", "updates", "diagnostics"], settings: true);
        Check(values.CollapsedSettings, ["service", "remote", "proxy", "advanced", "updates", "diagnostics"], settings: true);
    }
    private static void CheckCards(IReadOnlyList<string> cards)
    {
        if (cards is null || cards.Count > 64 || cards.Distinct(StringComparer.Ordinal).Count() != cards.Count ||
            cards.Any(value => value is null || !System.Text.RegularExpressions.Regex.IsMatch(value,
                "^[a-z][a-z0-9.-]{0,95}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
            throw new ArgumentException("interface_values_invalid");
    }
    private static void Check(IReadOnlyList<string> values, string[] allowed, bool settings = false)
    {
        if (values is null || values.Count > (settings ? 64 : allowed.Length) || values.Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Any(value => !allowed.Contains(value, StringComparer.Ordinal) && !(settings &&
                System.Text.RegularExpressions.Regex.IsMatch(value, "^extension:[a-z0-9-]{1,80}:[a-z0-9-]{1,40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100))))) throw new ArgumentException("interface_values_invalid");
    }
}
