using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ProviderLauncherEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal IReadOnlyList<BlobReference> RequireAll(AttemptWriterState state, bool verifyContents)
        => RequireAll(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, verifyContents);

    internal IReadOnlyList<BlobReference> RequireAll(JsonElement journal, ExecutionPlan total, string totalSha, bool verifyContents) =>
        ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current)
            .SelectMany(view => RequireCurrent(view, total, totalSha, verifyContents)).Distinct().ToArray();

    private IReadOnlyList<BlobReference> RequireCurrent(JsonElement journal, ExecutionPlan total, string totalSha, bool verifyContents)
    {
        var result = new List<BlobReference>();
        foreach (var item in ProviderLauncherCodec.ReadAll(journal, total, totalSha))
            result.AddRange(Require(journal, total, item, verifyContents));
        return result.Distinct().ToArray();
    }

    internal IReadOnlyList<BlobReference> Require(JsonElement journal, ExecutionPlan total, ProviderLauncherIntent item, bool verifyContents)
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
                    recovered.BeforeRevision < 0 || recovered.BeforeRevision >= journal.GetProperty("revision").GetInt64()) throw Invalid();
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

    private void RequireRetention(JsonElement journal, ExecutionPlan total, ProviderLauncherRetention document,
        ProcessIdentity? root, ExecutionPlanInterpreterImage image, List<BlobReference> references, bool verifyContents)
    {
        TargetJobEvidence.RequireInventory(document.Inventory);
        if (root is null || document.Root != root || document.BeforeRevision < 0 || document.BeforeRevision >= journal.GetProperty("revision").GetInt64() ||
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

    internal void RequireResolved(AttemptWriterState state, string? stepId = null) =>
        RequireResolved(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256, stepId);

    internal void RequireResolved(JsonElement journal, ExecutionPlan total, string sha, string? stepId = null)
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
