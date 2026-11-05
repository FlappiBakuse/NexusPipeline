namespace NexusPipeline.Core.Records;

public sealed record BindingTaskSummary(Guid BindingId, Guid UserId, Guid ScriptId, Guid? RunId,
    DateTimeOffset? EndedAt, string Tone, string Reason);
