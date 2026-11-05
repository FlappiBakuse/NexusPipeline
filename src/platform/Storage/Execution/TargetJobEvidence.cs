using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class TargetJobEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    ExecutionSqlScope? readScope = null)
{
    private ImmutableBlobStore? _blobs;
    private ImmutableBlobStore Blobs => _blobs ??= new(instance, contracts);

    internal IReadOnlyList<BlobReference> RequireAll(JsonElement journal, ExecutionPlan total, string totalPlanSha256) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => RequireCurrent(view, total, totalPlanSha256)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> RequireCurrent(JsonElement journal, ExecutionPlan total, string totalPlanSha256)
    {
        var references = new List<BlobReference>();
        foreach (var item in TargetJobCodec.ReadAll(journal, total, totalPlanSha256))
            references.AddRange(Require(new(item.Binding, item.Stage, item.Evidence), total, journal));
        return references.Distinct().ToArray();
    }

    internal void RequireResolved(JsonElement journal, ExecutionPlan total, string totalPlanSha256, string? stepId = null)
    {
        _ = RequireAll(journal, total, totalPlanSha256);
        foreach (var item in TargetJobCodec.Read(journal, total, totalPlanSha256).Where(item => stepId is null || item.Binding.StepId == stepId))
        {
            if (item.Stage is "retained" or "recoveredRetained") continue;
            if (item.Stage == "recoveredStopped")
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var recovered = Read<TargetRecoveryDocument>(item.Evidence!);
                using var native = PreparedRecoveryObservation.Capture(item.Binding.JobName, item.Binding.SessionId, recovered.Creator, recovered.Root, null);
                native.Use(item.Binding.JobName, item.Binding.SessionId, recovered.Creator, recovered.Root, _ =>
                { RequireStoppedFamily(recovered); return true; });
                continue;
            }
            if (item.Stage != "stopped") throw new IOException("execution_target_collection_pending");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var stopped = Read<TargetStoppedDocument>(item.Evidence!);
            if (WriterStopEvidence.ObserveJob(item.Binding.JobName, item.Binding.SessionId) == NexusPipeline.Core.Execution.WriterState.Running)
                throw new IOException("execution_target_stop_unconfirmed");
            foreach (var member in stopped.Inventory.AllFamily)
                if (WriterStopEvidence.ObserveProcess(member) != NexusPipeline.Core.Execution.WriterState.Stopped)
                    throw new IOException("execution_target_stop_unconfirmed");
        }
    }

    internal T Read<T>(BlobReference reference) where T : class
    {
        StepEvidenceCodec.Plan(reference);
        using var stream = Blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var result = parsed.Deserialize<T>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(result, StepResultCodec.Options))) throw Invalid();
        return result;
    }

    internal IReadOnlyList<BlobReference> Require(TargetJobState item, ExecutionPlan total, JsonElement journal)
    {
        var binding = item.Binding;
        var references = new List<BlobReference>();
        if (item.Stage == "registered") return references;
        var artifact = item.Evidence ?? throw Invalid(); references.Add(artifact);
        var pc = total.Steps.Single(step => step.StepId == binding.StepId).Target?.Pc ?? throw Invalid();
        var root = readScope is null ? TargetRootFacts.Read(database, binding) :
            readScope.Read(database, connection => TargetRootFacts.ReadInside(connection, null, binding));
        switch (item.Stage)
        {
            case "root":
                var started = Read<TargetRootDocument>(artifact);
                RequireHeader(started.Format, started.Version, started.Binding, "root", binding);
                if (started.Root is null || started.Root != root) throw Invalid();
                break;
            case "ready":
                var ready = Read<TargetReadyDocument>(artifact);
                RequireHeader(ready.Format, ready.Version, ready.Binding, "ready", binding);
                if (root is null || ready.Mode != (pc.ReadinessMode == "process" ? "Process" : "VisibleWindow") ||
                    ready.Members.IsDefaultOrEmpty || ready.Members.Length > 4096 ||
                    ready.Members.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != ready.Members.Length ||
                    ready.Members.Any(member => !SameImage(member, pc.ReadyImage))) throw Invalid();
                foreach (var member in ready.Members) RequireIdentity(member);
                break;
            case "stopped":
                var stopped = Read<TargetStoppedDocument>(artifact);
                RequireHeader(stopped.Format, stopped.Version, stopped.Binding, "stopped", binding);
                RequireInventory(stopped.Inventory);
                if (stopped.Root != root || stopped.Inventory.ActiveProcesses != 0 || !stopped.Inventory.LivingFamily.IsEmpty ||
                    root is not null && !stopped.Inventory.AllFamily.Contains(root)) throw Invalid();
                break;
            case "retentionPrepared":
                var intent = Read<TargetRetentionDocument>(artifact);
                RequireRetention(intent, binding, root, pc);
                RequireNecessary(intent, total, journal);
                references.AddRange(intent.NecessaryClosures); references.Add(intent.PrimaryResult!);
                break;
            case "retained":
                var retained = Read<TargetRetainedDocument>(artifact);
                RequireHeader(retained.Format, retained.Version, retained.Binding, "retained", binding);
                RequirePreviousRetention(journal, total, item, retained.Intent);
                StepEvidenceCodec.Plan(retained.Intent);
                var prepared = Read<TargetRetentionDocument>(retained.Intent);
                RequireRetention(prepared, binding, root, pc);
                RequireNecessary(prepared, total, journal);
                if (retained.Native is null || retained.Native.PreviousLimitFlags != 0x00002000 || retained.Native.EffectiveLimitFlags != 0 ||
                    !Same(prepared.Inventory, retained.Native.Inventory)) throw Invalid();
                references.Add(retained.Intent); references.AddRange(prepared.NecessaryClosures); references.Add(prepared.PrimaryResult!);
                break;
            case "recoveredStopped" or "recoveredRetained":
                var recovery = Read<TargetRecoveryDocument>(artifact);
                RequireRecovery(recovery, item, total, journal, root, pc);
                if (recovery.Intent is { } previous)
                {
                    var original = Read<TargetRetentionDocument>(previous);
                    references.Add(previous); references.AddRange(original.NecessaryClosures); references.Add(original.PrimaryResult!);
                }
                break;
            default: throw Invalid();
        }
        return references.Distinct().ToArray();
    }

    private void RequireRecovery(TargetRecoveryDocument value, TargetJobState item, ExecutionPlan total,
        JsonElement journal, ProcessIdentity? root, ExecutionTargetPc pc)
    {
        RequireHeader(value.Format, value.Version, value.Binding, "recovery", item.Binding);
        var observed = value.Observation;
        var creator = readScope is null ? TargetRootFacts.Creator(database, item.Binding) :
            readScope.Read(database, connection => TargetRootFacts.CreatorInside(connection, item.Binding));
        if (value.Creator != creator || value.Root != root || value.ObservedAt.Offset != TimeSpan.Zero ||
            value.ObservedAt.Year < 1601 || observed is null || observed.LivingFamily.IsDefault || observed.LivingFamily.Length > 4096 ||
            observed.LivingFamily.Any(member => member is null) || observed.LivingFamily.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != observed.LivingFamily.Length)
            throw Invalid();
        RequireIdentity(value.Creator);
        if (root is not null) RequireIdentity(root);
        foreach (var member in observed.LivingFamily) RequireIdentity(member);
        if (observed.Accounting is { } accounting && (!double.IsFinite(accounting.CpuSeconds) || accounting.CpuSeconds < 0 ||
            accounting.ActiveProcesses != observed.LivingFamily.Length || accounting.TotalProcesses < accounting.ActiveProcesses)) throw Invalid();
        if (item.Stage == "recoveredRetained")
        {
            if (value.Intent is null || root is null || observed.Kind != "retained" || observed.LimitFlags != 0 ||
                observed.Accounting is null || observed.LivingFamily.IsEmpty || observed.LivingFamily.Any(member => !SameImage(member, pc.ReadyImage))) throw Invalid();
            RequirePreviousRetention(journal, total, item, value.Intent);
            var prepared = Read<TargetRetentionDocument>(value.Intent);
            RequireRetention(prepared, item.Binding, root, pc); RequireNecessary(prepared, total, journal);
        }
        else
        {
            if (!observed.LivingFamily.IsEmpty || (observed.Kind switch
            {
                "missing" => observed.Accounting is not null || observed.LimitFlags is not null,
                "empty" => observed.Accounting is null || observed.Accounting.ActiveProcesses != 0 || observed.LimitFlags is not (0 or 0x00002000),
                _ => true
            })) throw Invalid();
            if (value.Intent is { } intent)
            {
                RequirePreviousRetention(journal, total, item, intent);
                var prepared = Read<TargetRetentionDocument>(intent);
                RequireRetention(prepared, item.Binding, root, pc); RequireNecessary(prepared, total, journal);
            }
            else if (Previous(journal, total, item)?.Stage == "retentionPrepared") throw Invalid();
        }
    }

    internal void RequireStoppedFamily(TargetRecoveryDocument value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (value.Intent is not { } reference) return;
        var previous = Read<TargetRetentionDocument>(reference);
        foreach (var member in previous.Inventory.AllFamily)
            if (WriterStopEvidence.ObserveProcess(member) != NexusPipeline.Core.Execution.WriterState.Stopped)
                throw new IOException("execution_target_stop_unconfirmed");
    }

    private static void RequirePreviousRetention(JsonElement journal, ExecutionPlan total, TargetJobState item, BlobReference intent)
    {
        var previous = Previous(journal, total, item);
        if (previous?.Stage != "retentionPrepared" || previous.Evidence != intent) throw Invalid();
    }

    private static TargetJobIntent? Previous(JsonElement journal, ExecutionPlan total, TargetJobState item) =>
        TargetJobCodec.ReadAll(journal, total, item.Binding.TotalPlanSha256)
            .TakeWhile(candidate => candidate.Stage != item.Stage || candidate.Evidence != item.Evidence)
            .LastOrDefault(candidate => candidate.Binding == item.Binding);

    private void RequireNecessary(TargetRetentionDocument intent, ExecutionPlan total, JsonElement journal)
    {
        var stepId = intent.Binding.StepId;
        var jobs = WriterIntentCodec.ReadJobs(journal, total).Where(job => job.StepId == stepId && job.Role != "postHook")
            .OrderBy(job => job.JobName, StringComparer.Ordinal).ToArray();
        var required = ExecutionBindingRoles.Required(total.Steps.Single(step => step.StepId == stepId)).Where(role => role != "postHook");
        var primary = StepResultCodec.Read(journal, total).SingleOrDefault(result => result.Binding.StepId == stepId);
        if (!jobs.Select(job => job.Role).ToHashSet(StringComparer.Ordinal).SetEquals(required) ||
            jobs.Any(job => job.ClosedElapsedMilliseconds is null) || primary is not { IsCollected: true } || primary.Latest.Artifact != intent.PrimaryResult)
            throw Invalid();
        var reader = new StepEvidenceReader(instance, database, contracts);
        if (!jobs.Select(job => reader.ClosureReference(StepResultCodec.Binding(journal, job))).SequenceEqual(intent.NecessaryClosures)) throw Invalid();
        foreach (var job in jobs)
        {
            var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(intent.Binding.ParentJournalId,
                job.WriterId, job.JobName, job.SessionId) ?? throw Invalid();
            if (closure.RunElapsedMilliseconds != job.ClosedElapsedMilliseconds) throw Invalid();
        }
        _ = reader.ReadReferences(primary.Binding, primary.Latest.Artifact);
    }

    private static void RequireRetention(TargetRetentionDocument value, ExecutionTargetBinding binding,
        ProcessIdentity? root, ExecutionTargetPc pc)
    {
        RequireHeader(value.Format, value.Version, value.Binding, "retention", binding);
        RequireInventory(value.Inventory);
        if (root is null || value.Root != root || !value.Inventory.AllFamily.Contains(root) || value.Inventory.LimitFlags != 0x00002000 ||
            value.Inventory.ActiveProcesses == 0 || value.Inventory.LivingFamily.Any(member => !SameImage(member, pc.ReadyImage)) ||
            value.TargetStage is not ("root" or "ready") || value.NecessaryClosures.IsDefaultOrEmpty ||
            value.NecessaryClosures.Length > 3 || value.NecessaryClosures.Distinct().Count() != value.NecessaryClosures.Length ||
            value.PrimaryResult is null) throw Invalid();
        foreach (var closure in value.NecessaryClosures) StepEvidenceCodec.Plan(closure);
        StepEvidenceCodec.Plan(value.PrimaryResult);
    }

    internal static void RequireInventory(OwnedTargetInventory inventory)
    {
        if (inventory is null || inventory.AllFamily.IsDefault || inventory.LivingFamily.IsDefault || inventory.AllFamily.Length > 4096 ||
            inventory.TotalProcesses != inventory.AllFamily.Length || inventory.TotalProcesses != inventory.RetainedKernelObjects ||
            inventory.ActiveProcesses != inventory.LivingFamily.Length || inventory.ActiveProcesses > inventory.TotalProcesses ||
            inventory.AllFamily.Any(member => member is null) || inventory.LivingFamily.Any(member => member is null) ||
            inventory.AllFamily.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != inventory.AllFamily.Length ||
            inventory.LivingFamily.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count() != inventory.LivingFamily.Length ||
            inventory.LivingFamily.Any(member => !inventory.AllFamily.Contains(member)) || inventory.LimitFlags is not (0 or 0x00002000)) throw Invalid();
        foreach (var member in inventory.AllFamily) RequireIdentity(member);
    }

    private static void RequireHeader(string format, int version, ExecutionTargetBinding actual, string suffix, ExecutionTargetBinding expected)
    { if (format != "nexus.execution.target-" + suffix || version != 1 || actual != expected) throw Invalid(); }

    private static bool SameImage(ProcessIdentity value, ExecutionPlanInterpreterImage image) => value is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(value.ImagePath, image.Path) && value.ImageSha256 == image.Sha256;

    internal static void RequireIdentity(ProcessIdentity value)
    {
        if (value is null || value.ProcessId < 1 || value.CreatedAtUtc.Kind != DateTimeKind.Utc || value.CreatedAtUtc.ToFileTimeUtc() <= 0 ||
            !Path.IsPathFullyQualified(value.ImagePath) || !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.ImageSha256) ||
            string.IsNullOrWhiteSpace(value.UserSid)) throw Invalid();
    }

    private static bool Same<T>(T first, T second) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first, StepResultCodec.Options),
        JsonSerializer.SerializeToElement(second, StepResultCodec.Options));
    private static IOException Invalid() => new("execution_target_evidence_unrecognized");
}
