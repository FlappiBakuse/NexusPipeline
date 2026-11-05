namespace NexusPipeline.Extensibility.Execution;

/// <summary>Registers the execution entry declared by the current verified extension package.</summary>
public interface IExecutionRegistrationPort
{
    /// <summary>The Host binds this registration to the current activation and its immutable package descriptor.</summary>
    ValueTask RegisterAsync(CancellationToken cancellationToken);
}
