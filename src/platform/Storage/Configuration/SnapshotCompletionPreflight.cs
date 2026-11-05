using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class SnapshotCompletionPreflight(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal void Verify(IReadOnlyList<RegisteredJournal> registered)
    {
        try
        {
            var owner = new SnapshotCompletionOwner(instance, database, contracts);
            var operations = new HashSet<Guid>(); var sessions = new HashSet<Guid>();
            foreach (var parent in registered.Where(item => item.Kind == JournalKind.RunAttempt))
            {
                var directory = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, "journals", parent.JournalId.ToString("D")));
                if (!Directory.Exists(directory))
                {
                    if (File.Exists(directory) || parent.Phase != JournalPhase.Prepared || parent.Revision != 1)
                        throw new IOException("configuration_snapshot_completion_parent_missing");
                    continue;
                }
                var bound = AttemptPreparedBinding.Read(instance, contracts, database, parent.JournalId);
                foreach (var intent in SnapshotIntentCodec.Read(JsonSerializer.SerializeToElement(bound.Document)))
                {
                    if (!operations.Add(intent.CompletionOperationId) || !sessions.Add(intent.SessionId))
                        throw new IOException("configuration_snapshot_completion_intent_unbound");
                    // The recognized Prepared file prefix may lead SQL; verification must not project it or issue a permit.
                    _ = owner.ReadVerifiedIntent(parent, intent);
                }
            }
        }
        catch (Exception error) when (error is ConfigurationException or OperationConflictException or InvalidOperationException or ArgumentException)
        { throw new IOException("configuration_snapshot_completion_intent_unbound", error); }
    }
}
