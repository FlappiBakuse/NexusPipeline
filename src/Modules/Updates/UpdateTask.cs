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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetImageHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessIdentity? WorkerIdentity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestartHandoffId { get; init; }

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
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(file).Length > 64 * 1024)
                throw new InvalidDataException("invalid update journal file");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            CheckMembers(document.RootElement);
            foreach (string required in new[] { nameof(Mode), nameof(Version), nameof(StagedDir), nameof(Phase), nameof(CreatedAt) })
                if (!document.RootElement.TryGetProperty(required, out _)) throw new InvalidDataException("missing update journal field: " + required);
            var task = JsonSerializer.Deserialize<UpdateTask>(document.RootElement, CurrentOptions)
                ?? throw new InvalidDataException("null update journal");
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
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("update journal object required");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in value.EnumerateObject())
        {
            if (!names.Add(member.Name)) throw new InvalidDataException("duplicate update journal field");
            if (member.Value.ValueKind == JsonValueKind.Object) CheckMembers(member.Value);
        }
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
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonUtil.WriteAtomic(file, JsonSerializer.Serialize(this, JsonOpts.Indented));
    }

    internal void Clear(string? path = null)
    {
        string file = path ?? AppPaths.UpdateTaskFile;
        var previous = Read(file);
        if (previous is null) return;
        if (previous != this) throw new InvalidDataException("update_journal_identity_changed");
        File.Delete(file);
    }
}
