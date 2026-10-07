using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

internal enum UpdateFileState { Missing, Current, Unsupported }
internal sealed record UpdateFileRead<T>(UpdateFileState State, T? Value, string? Error = null);

internal sealed record UpdateTask(string Mode, string Version, string StagedDir, string Phase, DateTimeOffset? CreatedAt)
{
    public int JournalSchemaVersion { get; init; } = 1;
    public DesktopResumeIntent? DesktopResumeIntent { get; init; }
    public string? WorkerPath { get; init; }
    public bool WorkerLaunchPending { get; init; }
    public UpdateInventory? StagingInventory { get; init; }
    public UpdateInventory? BackupInventory { get; init; }
    public string? WorkerSha256 { get; init; }
    public string? PackageChecksumSha256 { get; init; }
    public UpdatePreservation? Preservation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetImageHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessIdentity? WorkerIdentity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestartHandoffId { get; init; }
    public int PayloadSchemaVersion { get; init; } = 1;
    public UpdatePayloadIdentity? TargetPayload { get; init; }
    public UpdatePayloadIdentity? PreviousPayload { get; init; }
    public string? PackageSha256 { get; init; }
    public string PackageSource { get; init; } = "download";
    public bool DesktopStopped { get; init; }
    public int SwappedAssetCount { get; init; }

    internal static UpdateTask Create(string mode, string version, string staging) =>
        new(mode, version, staging, mode == "defer" ? UpdatePhase.Deferred : UpdatePhase.ApplyRequested, DateTimeOffset.UtcNow);

    private static readonly JsonSerializerOptions CurrentOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static UpdateFileRead<UpdateTask> ReadState(string? path = null)
    {
        string file = path ?? AppPaths.UpdateTaskFile;
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(file); }
            catch (FileNotFoundException) { return new(UpdateFileState.Missing, null); }
            catch (DirectoryNotFoundException) { return new(UpdateFileState.Missing, null); }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(file).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("invalid update journal file");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            CheckMembers(document.RootElement);
            foreach (string required in new[] { nameof(Mode), nameof(Version), nameof(StagedDir), nameof(Phase), nameof(CreatedAt),
                nameof(PayloadSchemaVersion), nameof(TargetPayload), nameof(PreviousPayload), nameof(PackageSha256), nameof(PackageSource), nameof(DesktopStopped), nameof(SwappedAssetCount),
                nameof(JournalSchemaVersion), nameof(StagingInventory), nameof(BackupInventory), nameof(WorkerSha256), nameof(PackageChecksumSha256), nameof(Preservation),
                nameof(DesktopResumeIntent), nameof(WorkerPath), nameof(WorkerLaunchPending) })
                if (!document.RootElement.TryGetProperty(required, out _)) throw new InvalidDataException("missing update journal field: " + required);
            var task = JsonSerializer.Deserialize<UpdateTask>(document.RootElement, CurrentOptions)
                ?? throw new InvalidDataException("null update journal");
            if (task.DesktopResumeIntent is not null)
                RequireFields(document.RootElement.GetProperty(nameof(DesktopResumeIntent)), "SchemaVersion", "Mode");
            foreach (string name in new[] { nameof(StagingInventory), nameof(BackupInventory) })
                if (document.RootElement.GetProperty(name) is { ValueKind: JsonValueKind.Object } inventory)
                {
                    RequireFields(inventory, "Entries");
                    foreach (var entry in inventory.GetProperty("Entries").EnumerateArray())
                        RequireFields(entry, "Path", "IsDirectory", "SizeBytes", "Sha256");
                }
            if (document.RootElement.GetProperty(nameof(Preservation)) is { ValueKind: JsonValueKind.Object } preservation)
            {
                RequireFields(preservation, "Files");
                foreach (var item in preservation.GetProperty("Files").EnumerateArray())
                {
                    RequireFields(item, "Area", "File");
                    RequireFields(item.GetProperty("File"), "Path", "IsDirectory", "SizeBytes", "Sha256");
                }
            }
            task.Validate();
            return new(UpdateFileState.Current, task);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return new(UpdateFileState.Unsupported, null, ex.Message); }
    }

    internal static UpdateTask? Read(string? path = null)
    {
        var result = ReadState(path);
        return result.State == UpdateFileState.Unsupported
            ? throw new InvalidDataException("unsupported_update_journal: " + result.Error) : result.Value;
    }

    private static void CheckMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) CheckMembers(item);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("update journal object required");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in value.EnumerateObject())
        {
            if (!names.Add(member.Name)) throw new InvalidDataException("duplicate update journal field");
            if (member.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) CheckMembers(member.Value);
        }
    }

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || fields.Any(field => !value.TryGetProperty(field, out _)))
            throw new InvalidDataException("update journal required field missing");
    }

    internal void Validate()
    {
        if (CreatedAt is null || CreatedAt == default(DateTimeOffset) || !NexusVersion.TryParse(Version, out _)
            || string.IsNullOrWhiteSpace(StagedDir) || !Path.IsPathFullyQualified(StagedDir)
            || Phase is not (UpdatePhase.Deferred or UpdatePhase.ApplyRequested or UpdatePhase.BackupPreparing
                or UpdatePhase.BackupReady or UpdatePhase.SwapInProgress or UpdatePhase.SwapReady or UpdatePhase.AwaitingStartup
                or UpdatePhase.Committed or UpdatePhase.RollbackPending or UpdatePhase.RollbackConfirmed)
            || Mode != (Phase == UpdatePhase.Deferred ? "defer" : Phase == UpdatePhase.Committed ? "completed" : "apply"))
            throw new InvalidDataException("unsupported_update_journal: invalid phase, time or identity");
        bool frozen = Phase is not (UpdatePhase.Deferred or UpdatePhase.ApplyRequested);
        if (Phase != UpdatePhase.Deferred && DesktopResumeIntent is null
            || Phase == UpdatePhase.Deferred && DesktopResumeIntent is not null
            || WorkerPath is not null && (!Path.IsPathFullyQualified(WorkerPath) || !UpdateInventory.Hash(WorkerSha256))
            || WorkerLaunchPending && WorkerPath is null)
            throw new InvalidDataException("unsupported_update_journal: preparation missing");
        DesktopResumeIntent?.Validate();
        if (JournalSchemaVersion != 1 || StagingInventory is null || frozen && BackupInventory is null
            || WorkerIdentity is not null && !UpdateInventory.Hash(WorkerSha256)
            || WorkerSha256 is not null && !UpdateInventory.Hash(WorkerSha256)
            || PackageChecksumSha256 is not null && !UpdateInventory.Hash(PackageChecksumSha256))
            throw new InvalidDataException("unsupported_update_journal: ownership missing");
        StagingInventory.Validate();
        BackupInventory?.Validate();
        Preservation?.Validate();
        static bool Hash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
        static bool Payload(UpdatePayloadIdentity? value) => value is not null && Hash(value.ManifestSha256) && Hash(value.BuildId)
            && Hash(value.FrontendHash) && value.Generation == InstallationGeneration.Id;
        if (PayloadSchemaVersion != 1 || !Payload(TargetPayload) || !Hash(PackageSha256) || PackageSource is not ("download" or "installer")
            || frozen && (!Payload(PreviousPayload) || !DesktopStopped)
            || SwappedAssetCount is < 0 or > 4
            || Phase is (UpdatePhase.Deferred or UpdatePhase.ApplyRequested or UpdatePhase.BackupPreparing or UpdatePhase.BackupReady) && SwappedAssetCount != 0
            || Phase is (UpdatePhase.SwapReady or UpdatePhase.AwaitingStartup or UpdatePhase.Committed) && SwappedAssetCount != 4)
            throw new InvalidDataException("unsupported_update_journal: application identity missing");
        if ((TransactionId is null) != (TargetImageHash is null)
            || TransactionId is not null && (!Guid.TryParseExact(TransactionId, "N", out _)
                || TargetImageHash is not { Length: 64 } || TargetImageHash.Any(ch => !Uri.IsHexDigit(ch)))
            || frozen && (TransactionId is null || WorkerIdentity is null)
            || WorkerIdentity is { } worker && (worker.Pid <= 0 || worker.StartTime == default || !Path.IsPathFullyQualified(worker.ImageName))
            || RestartHandoffId is not null && !Guid.TryParseExact(RestartHandoffId, "N", out _))
            throw new InvalidDataException("unsupported_update_journal: invalid transaction identity");
    }

    public void Write(string? path = null)
    {
        Validate();
        string file = path ?? AppPaths.UpdateTaskFile;
        var previous = Read(file);
        if (previous is not null && (previous.CreatedAt != CreatedAt || previous.Version != Version || previous.StagedDir != StagedDir
            || previous.TransactionId is not null && previous.TransactionId != TransactionId
            || previous.TargetImageHash is not null && previous.TargetImageHash != TargetImageHash))
            throw new InvalidDataException("update_journal_identity_changed");
        if (previous is not null && (previous.TargetPayload != TargetPayload || previous.PackageSha256 != PackageSha256 || previous.PackageSource != PackageSource
            || previous.PreviousPayload is not null && previous.PreviousPayload != PreviousPayload
            || previous.StagingInventory != StagingInventory || previous.PackageChecksumSha256 != PackageChecksumSha256
            || previous.DesktopResumeIntent is not null && previous.DesktopResumeIntent != DesktopResumeIntent
            || previous.BackupInventory is not null && previous.BackupInventory != BackupInventory
            || previous.Preservation is not null && (Preservation is null || previous.Preservation.Files.Any(entry => !Preservation.Files.Contains(entry)))
            || SwappedAssetCount < previous.SwappedAssetCount))
            throw new InvalidDataException("update_journal_payload_changed");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string bytes = JsonSerializer.Serialize(this, JsonOpts.Indented);
        if (System.Text.Encoding.UTF8.GetByteCount(bytes) > 2 * 1024 * 1024) throw new InvalidDataException("update.journal_limit");
        JsonUtil.WriteAtomic(file, bytes);
    }

    internal void Clear(string? path = null)
    {
        string file = path ?? AppPaths.UpdateTaskFile;
        var previous = Read(file);
        if (previous is null) return;
        if (previous != this) throw new InvalidDataException("update_journal_identity_changed");
        byte[] bytes = File.ReadAllBytes(file);
        if (Read(file) != this) throw new InvalidDataException("update_journal_identity_changed");
        VerifiedFileDeletion.Delete(file, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
}
