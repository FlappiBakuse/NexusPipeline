using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderLauncherBoundary(ImmutableArray<BlobReference> Closures,
    BlobReference Primary, BlobReference Terminal);

internal static class LauncherBoundaryReader
{
    [SupportedOSPlatform("windows")]
    internal static ProviderLauncherBoundary Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptWriterState state, string stepId)
    {
        var step = state.Total.Steps.Single(step => step.StepId == stepId);
        var required = ExecutionBindingRoles.Required(step).Where(role => role != "postHook").ToHashSet(StringComparer.Ordinal);
        var jobs = state.Jobs.Where(job => job.StepId == stepId).OrderBy(job => job.JobName, StringComparer.Ordinal).ToArray();
        if (required.Count == 0 || !jobs.Select(job => job.Role).ToHashSet(StringComparer.Ordinal).SetEquals(required))
            throw Invalid();
        StepWritersEvidence.RequireStopped(instance, database, contracts, state.Journal.JournalId, jobs);
        var primary = StepResultCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding.StepId == stepId);
        if (primary is not { IsCollected: true }) throw Invalid();
        var reader = new StepEvidenceReader(instance, database, contracts);
        _ = reader.Read(primary.Binding, primary.Latest.Artifact, collected: true);
        var references = ImmutableArray.CreateBuilder<BlobReference>();
        foreach (var job in jobs)
        {
            var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(
                state.Journal.JournalId, job.WriterId, job.JobName, job.SessionId) ?? throw Invalid();
            if (job.Role == "process" && (!closure.WasExecuted || closure.Root is null || closure.Family.IsDefaultOrEmpty)) throw Invalid();
            references.Add(reader.ClosureReference(StepResultCodec.Binding(state.Latest.Root, job)));
        }
        var terminal = ProviderTerminalCodec.Read(state.Latest.Root, state.Total).SingleOrDefault(item => item.Binding == primary.Binding)
            ?? throw Invalid();
        var replay = new ProviderControlReader(instance, database, contracts).Read(primary.Binding, terminal.Artifact);
        if (replay.Terminal != terminal.Decision || primary.Latest.Artifact is null) throw Invalid();
        return new(references.ToImmutable(), primary.Latest.Artifact, terminal.Artifact);
    }

    private static IOException Invalid() => new("execution_provider_launcher_necessary_boundary_pending");
}
