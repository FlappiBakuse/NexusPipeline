namespace NexusPipeline.Core.Execution;

public abstract record ExecutionTarget;
public sealed record BindingTarget(Guid BindingId) : ExecutionTarget;
public sealed record ProjectInitializationTarget(Guid BindingId) : ExecutionTarget;
public sealed record ScriptTarget(Guid ScriptId) : ExecutionTarget;
public sealed record ScriptUserTarget(Guid ScriptId, Guid UserId) : ExecutionTarget;
public sealed record QueueTarget(Guid QueueId) : ExecutionTarget;

public sealed record ExecutionSelectionRequest(ExecutionTarget Target, long ExpectedCatalogRevision, long ExpectedSettingsRevision);
public sealed class ExecutionSelectionException(string code) : Exception(code);
