using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ProviderLauncherEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal IReadOnlyList<BlobReference> RequireAll(AttemptWriterState state, bool verifyContents)
        => RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, verifyContents);

    internal IReadOnlyList<BlobReference> RequireAll(AttemptJournalView journal, ExecutionPlan total, string totalSha, bool verifyContents) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => RequireCurrent(view, total, totalSha, verifyContents)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> RequireCurrent(AttemptJournalView journal, ExecutionPlan total, string totalSha, bool verifyContents)
    {
        var result = new List<BlobReference>();
        foreach (var item in ProviderLauncherCodec.ReadAll(journal, total, totalSha))
            result.AddRange(Require(journal, total, item, verifyContents));
        return result.Distinct().ToArray();
    }

    internal IReadOnlyList<BlobReference> Require(AttemptJournalView journal, ExecutionPlan total, ProviderLauncherIntent item, bool verifyContents)
    {
        var references = new List<BlobReference>();
        if (item.Stage == "registered") return references;
        var artifact = item.Artifact ?? throw Invalid(); references.Add(artifact);
        var binding = item.Binding;
        var root = ProviderLauncherRows.Root(database, binding);
        var image = total.Steps.Single(step => step.StepId == binding.StepId).Provider!.Launcher!.Image;
        if (root is not null && (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image.Path) || root.ImageSha256 != image.Sha256))
            throw Invalid();
        switch (item.Stage)
        {
            case "root":
                var started = Read<ProviderLauncherRoot>(artifact);
                Header(started.Format, started.Version, started.Binding, "root", binding);
                if (started.Root is null || started.Root != root) throw Invalid();
                break;
            case "started":
                var resumed = Read<ProviderLauncherStarted>(artifact);
                Header(resumed.Format, resumed.Version, resumed.Binding, "started", binding);
                if (resumed.Root is null || resumed.Root != root) throw Invalid();
                break;
            case "stopped":
                var stopped = Read<ProviderLauncherStopped>(artifact);
                Header(stopped.Format, stopped.Version, stopped.Binding, "stopped", binding);
                TargetJobEvidence.RequireInventory(stopped.Inventory);
                if (stopped.Root != root || stopped.Inventory.ActiveProcesses != 0 || !stopped.Inventory.LivingFamily.IsEmpty ||
                    root is not null && !stopped.Inventory.AllFamily.Contains(root) || stopped.StartReturned && root is null ||
                    stopped.StartReturned && stopped.NativeStartErrorCode is not null || stopped.NativeStartErrorCode is < 1 ||
                    item.LaunchEngine != (stopped.StartReturned ? NexusPipeline.Core.Execution.EngineOutcome.Succeeded :
                        stopped.Inventory.TotalProcesses != 0 ? NexusPipeline.Core.Execution.EngineOutcome.Unknown :
                        stopped.NativeStartErrorCode is not null ? NexusPipeline.Core.Execution.EngineOutcome.Failed : NexusPipeline.Core.Execution.EngineOutcome.NotStarted)) throw Invalid();
                break;
            case "retentionPrepared":
                var prepared = Read<ProviderLauncherRetention>(artifact);
                Header(prepared.Format, prepared.Version, prepared.Binding, "retention", binding);
                RequireRetention(journal, total, prepared, root, image, references, verifyContents);
                break;
            case "retained":
                var retained = Read<ProviderLauncherRetained>(artifact);
                Header(retained.Format, retained.Version, retained.Binding, "retained", binding);
                StepEvidenceCodec.Plan(retained.Intent); references.Add(retained.Intent);
                var intent = Read<ProviderLauncherRetention>(retained.Intent);
                Header(intent.Format, intent.Version, intent.Binding, "retention", binding);
                RequireRetention(journal, total, intent, root, image, references, verifyContents);
                if (!Same(retained.Native.Inventory, intent.Inventory) ||
                    retained.Native.PreviousLimitFlags != 0x00002000 || retained.Native.EffectiveLimitFlags != 0) throw Invalid();
                if (ProviderLauncherRows.Ended(database, binding, artifact) is { } ended)
                {
                    references.Add(ended);
                    var leaf = Read<ProviderLauncherEnded>(ended);
                    Header(leaf.Format, leaf.Version, leaf.Binding, "ended", binding);
                    TargetJobEvidence.RequireInventory(leaf.Inventory);
                    if (leaf.Retained != artifact || leaf.Inventory.ActiveProcesses != 0 || leaf.Inventory.LimitFlags != 0 ||
                        !leaf.Inventory.LivingFamily.IsEmpty || !intent.Inventory.AllFamily.All(leaf.Inventory.AllFamily.Contains)) throw Invalid();
                }
                break;
            case "recoveredStopped":
            case "recoveredRetained":
                var recovered = Read<ProviderLauncherRecovered>(artifact);
                Header(recovered.Format, recovered.Version, recovered.Binding, "recovered", binding);
                if (recovered.Creator != ProviderLauncherRows.Creator(database, binding) || recovered.Root != root ||
                    recovered.BeforeRevision < 0 || recovered.BeforeRevision >= journal.Root.GetProperty("revision").GetInt64()) throw Invalid();
                var prior = new JournalStore(instance, contracts).ReadAllRevisions(binding.ParentJournalId)
                    .SingleOrDefault(revision => revision.Revision == recovered.BeforeRevision) ?? throw Invalid();
                var previous = ProviderLauncherCodec.Read(prior.Root, total, binding.TotalPlanSha256).SingleOrDefault(job => job.Binding == binding) ?? throw Invalid();
                var wasStarted = ProviderLauncherCodec.ReadAll(prior.Root, total, binding.TotalPlanSha256)
                    .Any(job => job.Binding == binding && job.Stage == "started");
                if (previous.Stage == "retentionPrepared")
                {
                    if (recovered.Intent != previous.Artifact || recovered.Observation.Kind == "missing") throw Invalid();
                    references.Add(recovered.Intent!);
                    var saved = Read<ProviderLauncherRetention>(recovered.Intent!);
                    RequireRetention(journal, total, saved, root, image, references, verifyContents);
                }
                else if (recovered.Intent is not null || previous.Stage is not ("registered" or "root" or "started")) throw Invalid();
                var observation = recovered.Observation;
                if (observation.LivingFamily.IsDefault || observation.LivingFamily.Length > 4096 ||
                    observation.LivingFamily.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != observation.LivingFamily.Length) throw Invalid();
                if (item.Stage == "recoveredRetained")
                {
                    if (recovered.Intent is null || root is null || observation.Kind != "retained" || observation.Accounting is null ||
                        observation.LimitFlags != 0 || observation.Accounting.ActiveProcesses == 0 ||
                        observation.Accounting.ActiveProcesses != observation.LivingFamily.Length ||
                        observation.Accounting.TotalProcesses < observation.Accounting.ActiveProcesses ||
                        observation.LivingFamily.Any(member => member.UserSid != root.UserSid || member.IsElevated != root.IsElevated ||
                            !StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, image.Path) || member.ImageSha256 != image.Sha256)) throw Invalid();
                }
                else
                {
                    if (!observation.LivingFamily.IsEmpty || item.LaunchEngine != (wasStarted ?
                        NexusPipeline.Core.Execution.EngineOutcome.Succeeded : NexusPipeline.Core.Execution.EngineOutcome.Unknown)) throw Invalid();
                    if (observation.Kind == "missing")
                    { if (recovered.Intent is not null || observation.Accounting is not null || observation.LimitFlags is not null) throw Invalid(); }
                    else if (observation.Kind != "empty" || observation.Accounting is null || observation.Accounting.ActiveProcesses != 0 ||
                        observation.LimitFlags is not (0 or 0x00002000) || recovered.Intent is null && observation.LimitFlags != 0x00002000) throw Invalid();
                }
                break;
            default: throw Invalid();
        }
        return references;
    }

    private void RequireRetention(AttemptJournalView journal, ExecutionPlan total, ProviderLauncherRetention document,
        ProcessIdentity? root, ExecutionPlanInterpreterImage image, List<BlobReference> references, bool verifyContents)
    {
        TargetJobEvidence.RequireInventory(document.Inventory);
        if (root is null || document.Root != root || document.BeforeRevision < 0 || document.BeforeRevision >= journal.Root.GetProperty("revision").GetInt64() ||
            document.Inventory.ActiveProcesses == 0 || document.Inventory.LimitFlags != 0x00002000 ||
            !document.Inventory.AllFamily.Contains(root) ||
            document.Inventory.LivingFamily.Any(member => member.UserSid != root.UserSid || member.IsElevated != root.IsElevated ||
                !StringComparer.OrdinalIgnoreCase.Equals(member.ImagePath, image.Path) || member.ImageSha256 != image.Sha256) ||
            document.NecessaryClosures.IsDefaultOrEmpty || document.NecessaryClosures.Length > 3 || document.References.IsDefaultOrEmpty ||
            !document.References.SequenceEqual(document.References.OrderBy(claim => claim.ResourceKey, StringComparer.Ordinal))) throw Invalid();
        ProviderLauncherReferences.RequireSubset(total, document.References, document.Binding.ExtensionId);
        foreach (var reference in document.NecessaryClosures.Append(document.PrimaryResult).Append(document.Terminal))
        { StepEvidenceCodec.Plan(reference); references.Add(reference); }
        var jobs = WriterIntentCodec.ReadJobs(journal, total).Where(job => job.StepId == document.Binding.StepId && job.Role != "postHook").OrderBy(job => job.JobName, StringComparer.Ordinal).ToArray();
        var required = ExecutionBindingRoles.Required(total.Steps.Single(step => step.StepId == document.Binding.StepId))
            .Where(role => role != "postHook").ToHashSet(StringComparer.Ordinal);
        if (!jobs.Select(job => job.Role).ToHashSet(StringComparer.Ordinal).SetEquals(required)) throw Invalid();
        var reader = new StepEvidenceReader(instance, database, contracts);
        if (!jobs.Select(job => reader.ClosureReference(StepResultCodec.Binding(journal, job))).SequenceEqual(document.NecessaryClosures))
            throw Invalid();
        var primary = StepResultCodec.Read(journal, total).SingleOrDefault(item => item.Binding.StepId == document.Binding.StepId);
        var terminal = ProviderTerminalCodec.Read(journal, total).SingleOrDefault(item => item.Binding.StepId == document.Binding.StepId);
        if (primary is not { IsCollected: true } || primary.Latest.Artifact != document.PrimaryResult || terminal?.Artifact != document.Terminal ||
            terminal.BeforeRevision > document.BeforeRevision) throw Invalid();
        var previous = new JournalStore(instance, contracts).ReadAllRevisions(document.Binding.ParentJournalId)
            .SingleOrDefault(revision => revision.Revision == document.BeforeRevision) ?? throw Invalid();
        var priorJobs = WriterIntentCodec.ReadJobs(previous.Root, total).Where(job => job.StepId == document.Binding.StepId).ToArray();
        if (ProviderLauncherCodec.Read(previous.Root, total, document.Binding.TotalPlanSha256)
                .SingleOrDefault(item => item.Binding == document.Binding)?.Stage is not ("started" or "retentionPrepared") ||
            !priorJobs.Select(job => job.Role).ToHashSet(StringComparer.Ordinal).SetEquals(required) ||
            StepResultCodec.Read(previous.Root, total).SingleOrDefault(result => result.Binding.StepId == document.Binding.StepId)
                is not { IsCollected: true } savedPrimary || savedPrimary.Latest.Artifact != document.PrimaryResult ||
            ProviderTerminalCodec.Read(previous.Root, total).SingleOrDefault(result => result.Binding.StepId == document.Binding.StepId)?.Artifact != document.Terminal)
            throw Invalid();
        references.AddRange(reader.ReadReferences(primary.Binding, primary.Latest.Artifact));
        references.AddRange(new ProviderControlReader(instance, database, contracts).ReadReferences(primary.Binding, document.Terminal));
        if (verifyContents)
        {
            _ = reader.Read(primary.Binding, primary.Latest.Artifact, collected: true);
            if (new ProviderControlReader(instance, database, contracts).Read(primary.Binding, document.Terminal).Terminal != terminal.Decision) throw Invalid();
        }
    }

    internal void RequireResolved(AttemptJournalView journal, ExecutionPlan total, string sha, string? stepId = null)
    {
        _ = RequireAll(journal, total, sha, verifyContents: true);
        foreach (var item in ProviderLauncherCodec.Read(journal, total, sha).Where(item => stepId is null || item.Binding.StepId == stepId))
        {
            if (item.Stage is "retained" or "recoveredRetained") continue;
            if (item.Stage == "recoveredStopped")
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var recovered = Read<ProviderLauncherRecovered>(item.Artifact!);
                using var native = PreparedRecoveryObservation.Capture(item.Binding.JobName, item.Binding.SessionId, recovered.Creator, recovered.Root, null);
                native.Use(item.Binding.JobName, item.Binding.SessionId, recovered.Creator, recovered.Root, _ =>
                {
                    if (recovered.Intent is { } intent && Read<ProviderLauncherRetention>(intent).Inventory.AllFamily.Any(member =>
                        !IsStopped(member)))
                        throw new IOException("execution_provider_launcher_stop_unconfirmed");
                    return true;
                });
                continue;
            }
            if (item.Stage != "stopped" || !OperatingSystem.IsWindows()) throw new IOException("execution_provider_launcher_collection_pending");
            var stopped = Read<ProviderLauncherStopped>(item.Artifact!);
            if (WriterStopEvidence.ObserveJob(item.Binding.JobName, item.Binding.SessionId) == NexusPipeline.Core.Execution.WriterState.Running ||
                stopped.Inventory.AllFamily.Any(member => !IsStopped(member)))
                throw new IOException("execution_provider_launcher_stop_unconfirmed");
        }
    }

    private static bool IsStopped(ProcessIdentity member)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return WriterStopEvidence.ObserveProcess(member) == NexusPipeline.Core.Execution.WriterState.Stopped;
    }

    internal T Read<T>(BlobReference reference) where T : class =>
        new TargetJobEvidence(instance, database, contracts).Read<T>(reference);

    private static void Header(string format, int version, ProviderLauncherBinding actual, string suffix, ProviderLauncherBinding expected)
    { if (format != "nexus.execution.provider-launcher-" + suffix || version != 1 || actual != expected) throw Invalid(); }
    private static bool Same<T>(T first, T second) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(first, StepResultCodec.Options), JsonSerializer.SerializeToElement(second, StepResultCodec.Options));
    private static IOException Invalid() => new("execution_provider_launcher_evidence_unbound");
}

internal static class ProviderLauncherCodec
{
    internal const string Format = "nexus.execution.provider-launcher";
    internal const int MaximumRetentionIntents = 3;

    internal static bool IsParticipant(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        if (!identity.TryGetProperty("format", out var format) || format.GetString() != Format) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<ProviderLauncherIntent> ReadAll(AttemptJournalView journal, ExecutionPlan total, string totalSha)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var result = ImmutableArray.CreateBuilder<ProviderLauncherIntent>();
        var states = new Dictionary<string, ProviderLauncherIntent>(StringComparer.Ordinal);
        foreach (var participant in journal.Participants.Where(IsParticipant))
        {
            var item = Decode(participant); var binding = item.Binding;
            var step = total.Steps.SingleOrDefault(step => step.StepId == binding.StepId) ?? throw Invalid();
            if (binding.InstanceId.ToString("D") != total.InstanceId ||
                binding.ParentJournalId.ToString("D") != journal.Root.GetProperty("journalId").GetString() ||
                binding.ParentOperationId.ToString("D") != journal.Root.GetProperty("operationId").GetString() ||
                binding.AttemptId.ToString("D") != journal.Root.GetProperty("attemptId").GetString() ||
                binding.TotalPlanSha256 != totalSha || step.EngineKind != "extensionWorker" ||
                step.Provider is not { Launcher: not null } provider || provider.ExtensionId != binding.ExtensionId ||
                provider.ActivationId != binding.ActivationId.ToString("D") || provider.EntryId != binding.EntryId ||
                provider.Launcher.WritesManagedConfiguration || provider.Launcher.TerminalProtocol != "worker-terminal/1" ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            if (item.Stage == "registered")
            {
                if (item.Artifact is not null || states.ContainsKey(binding.StepId) ||
                    states.Values.Any(prior => prior.Binding.JobName == binding.JobName || prior.Binding.LauncherId == binding.LauncherId)) throw Invalid();
            }
            else if (!states.TryGetValue(binding.StepId, out var before) || before.Binding != binding ||
                item.Artifact is null || !Transition(before.Stage, item.Stage)) throw Invalid();
            if (item.Stage == "retentionPrepared" &&
                result.Count(prior => prior.Binding == binding && prior.Stage == "retentionPrepared") >= MaximumRetentionIntents) throw Invalid();
            states[binding.StepId] = item; result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<ProviderLauncherIntent> ReadHistory(AttemptJournalView journal, ExecutionPlan total, string sha)
    {
        foreach (var view in ExecutionInvocationCodec.Views(journal)) _ = ReadAll(view, total, sha);
        return journal.Participants.Where(IsParticipant).Select(Decode).ToImmutableArray();
    }

    internal static ImmutableArray<ProviderLauncherState> Read(AttemptJournalView journal, ExecutionPlan total, string sha) =>
        ReadAll(journal, total, sha).GroupBy(item => item.Binding.StepId, StringComparer.Ordinal)
            .Select(group => group.Last()).Select(item => new ProviderLauncherState(item.Binding, item.Stage, item.Artifact, item.LaunchEngine)).ToImmutableArray();

    internal static JsonObject Participant(ProviderLauncherIntent value, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "process", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(value, StepResultCodec.Options)
    };

    internal static void RequireResolved(AttemptJournalView journal, ExecutionPlan total, string sha, string? stepId = null)
    {
        if (Read(journal, total, sha).Any(item => (stepId is null || item.Binding.StepId == stepId) &&
            item.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained"))) throw new IOException("execution_provider_launcher_collection_pending");
    }

    private static ProviderLauncherIntent Decode(JsonElement participant)
    {
        var json = participant.GetProperty("identity");
        var value = json.Deserialize<ProviderLauncherIntent>(StepResultCodec.Options) ?? throw Invalid();
        var binding = value.Binding ?? throw Invalid();
        if (!JsonElement.DeepEquals(json, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || participant.GetProperty("kind").GetString() != "process" ||
            binding.InstanceId == Guid.Empty || binding.ParentJournalId == Guid.Empty || binding.ParentOperationId == Guid.Empty ||
            binding.AttemptId == Guid.Empty || binding.LauncherId == Guid.Empty || binding.OwnerWriterId == Guid.Empty ||
            binding.ActivationId == Guid.Empty || binding.SessionId < 0 || string.IsNullOrWhiteSpace(binding.StepId) ||
            string.IsNullOrWhiteSpace(binding.ExtensionId) || string.IsNullOrWhiteSpace(binding.EntryId) ||
            binding.TotalPlanSha256 is not { Length: 64 } || binding.TotalPlanSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            binding.JobName is not { Length: 50 } || !binding.JobName.StartsWith("Local\\NexusWorker-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(binding.JobName[18..], "N", out var job) || job == Guid.Empty || binding.JobName[18..] != job.ToString("N") ||
            value.Stage is not ("registered" or "root" or "started" or "retentionPrepared" or "retained" or "stopped" or "recoveredStopped" or "recoveredRetained")) throw Invalid();
        if (value.Stage is "stopped" or "recoveredStopped" ? value.LaunchEngine is not (EngineOutcome.NotStarted or EngineOutcome.Succeeded or EngineOutcome.Failed or EngineOutcome.Unknown) :
            value.LaunchEngine is not null) throw Invalid();
        if (value.Artifact is not null) StepEvidenceCodec.Plan(value.Artifact);
        return value;
    }

    private static bool Transition(string before, string after) => (before, after) switch
    {
        ("registered", "root") or ("root", "started") or ("started", "retentionPrepared") or ("retentionPrepared", "retentionPrepared") or
        ("retentionPrepared", "retained") or ("registered" or "root" or "started" or "retentionPrepared", "stopped") or
        ("registered" or "root" or "started" or "retentionPrepared", "recoveredStopped") or ("retentionPrepared", "recoveredRetained") => true,
        _ => false
    };

    private static IOException Invalid() => new("execution_provider_launcher_unrecognized");
}

internal static class ProviderLauncherProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var old = ProviderLauncherCodec.ReadHistory(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var current = ProviderLauncherCodec.ReadHistory(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        foreach (var item in current.Skip(old.Length))
        {
            if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
                before.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined) || next.Phase != before.Phase) throw Invalid();
            if (item.Stage == "registered")
            {
                RequireNewEffectInside(connection, transaction, state, item.Binding.StepId);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writers WHERE journal_id=$journal AND writer_id=$writer);";
                command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
                command.Parameters.AddWithValue("$writer", item.Binding.OwnerWriterId.ToString("D"));
                if ((long)command.ExecuteScalar()! != 1) throw Invalid();
            }
            else if (item.Stage is not ("stopped" or "recoveredStopped") && ProviderLauncherRows.RootInside(connection, transaction, item.Binding) is null) throw Invalid();
        }
    }

    internal static void RequireNewEffectInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, string stepId)
    {
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
        if (!state.IsExecutionStart || state.Journal.Phase != JournalPhase.Applying) throw Invalid();
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
        ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, stepId, "process", state.Latest.Root);
        PcStageProjection.RequireConfigurationInstalledInside(connection, transaction, state, stepId);
        var primary = state.Jobs.Single(job => job.StepId == stepId && job.Role == "process");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM main.journal_writer_job_roots WHERE journal_id=$journal AND job_name=$job);";
        command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$job", primary.JobName);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, state.Latest,
            state.Total.Steps.ToList().FindIndex(step => step.StepId == stepId));
    }

    internal static void RequireMainInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, StepWriterState job)
    {
        if (job.Role is not ("process" or "judgement") ||
            state.Total.Steps.Single(step => step.StepId == job.StepId).Provider?.Launcher is null) return;
        var launcher = ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == job.StepId);
        if (launcher?.Stage != "started" || ProviderLauncherRows.RootInside(connection, transaction, launcher.Binding) is null) throw Invalid();
    }

    private static IOException Invalid() => new("execution_provider_launcher_projection_unbound");
}

internal sealed record ProviderLauncherBinding(Guid InstanceId, Guid ParentJournalId, Guid ParentOperationId,
    Guid AttemptId, string StepId, Guid LauncherId, string JobName, Guid OwnerWriterId, int SessionId,
    string TotalPlanSha256, Guid ActivationId, string ExtensionId, string EntryId);

internal sealed record ProviderLauncherIntent(string Format, int Version, ProviderLauncherBinding Binding,
    string Stage, BlobReference? Artifact, EngineOutcome? LaunchEngine = null);

internal sealed record ProviderLauncherRoot(string Format, int Version,
    ProviderLauncherBinding Binding, ProcessIdentity Root);

internal sealed record ProviderLauncherStopped(string Format, int Version,
    ProviderLauncherBinding Binding, ProcessIdentity? Root, OwnedTargetInventory Inventory, bool StartReturned, int? NativeStartErrorCode);

internal sealed record ProviderLauncherRetention(string Format, int Version, ProviderLauncherBinding Binding,
    ProcessIdentity Root, OwnedTargetInventory Inventory, ImmutableArray<BlobReference> NecessaryClosures,
    BlobReference PrimaryResult, BlobReference Terminal, ImmutableArray<ResourceClaim> References, long BeforeRevision);

internal sealed record ProviderLauncherRetained(string Format, int Version, ProviderLauncherBinding Binding,
    BlobReference Intent, OwnedTargetHandoff Native);

internal sealed record ProviderLauncherEnded(string Format, int Version, ProviderLauncherBinding Binding,
    BlobReference Retained, OwnedTargetInventory Inventory);

internal sealed record ProviderLauncherState(ProviderLauncherBinding Binding, string Stage, BlobReference? Artifact, EngineOutcome? LaunchEngine);

internal sealed record ProviderLauncherStarted(string Format, int Version,
    ProviderLauncherBinding Binding, ProcessIdentity Root);

internal sealed record ProviderLauncherRecovered(string Format, int Version, ProviderLauncherBinding Binding,
    ProcessIdentity Creator, ProcessIdentity? Root, BlobReference? Intent, TargetRecoveryObservation Observation, long BeforeRevision);
