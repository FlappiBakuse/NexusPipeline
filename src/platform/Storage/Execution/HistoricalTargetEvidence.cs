using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class HistoricalTargetEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    ExecutionPlan total, string totalSha256)
{
    private readonly Dictionary<TargetJobState, JsonElement> _targets = [];
    private readonly Dictionary<PcStageIntent, JsonElement> _stages = [];

    internal void Observe(JsonElement journal)
    {
        foreach (var view in ExecutionInvocationCodec.Views(journal).Select(ExecutionInvocationCodec.Current))
        {
            foreach (var item in TargetJobCodec.ReadAll(view, total, totalSha256))
                _targets.TryAdd(new(item.Binding, item.Stage, item.Evidence), view);
            foreach (var item in PcStageCodec.Read(view, total, totalSha256)) _stages.TryAdd(item, view);
        }
    }

    internal void Verify()
    {
        using var sql = new ExecutionSqlScope(database);
        var targets = new TargetJobEvidence(instance, database, contracts, sql);
        // Append-only revisions share intents; retain the first complete context for every historical artifact.
        foreach (var (item, context) in _targets) _ = targets.Require(item, total, context);
        var stages = new PcStageEvidence(instance, database, contracts, sql);
        foreach (var context in _stages.Values.Distinct()) _ = stages.RequireAll(context, total, totalSha256);
    }
}
