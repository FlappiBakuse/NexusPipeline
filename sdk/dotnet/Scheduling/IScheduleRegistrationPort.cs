using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Scheduling;

/// <summary>Registers work providers; occurrence identity, timing, attempts and retry budgets remain owned by the Host.</summary>
public interface IScheduleRegistrationPort
{
    /// <summary>Registers within the current activation; partial activation remains invisible and is rolled back on failure.</summary>
    ValueTask RegisterAsync(string providerId, IScheduledWorkHandler handler, CancellationToken cancellationToken);
}

/// <summary>Handles a frozen Host invocation without creating a scheduling or retry loop.</summary>
public interface IScheduledWorkHandler
{
    /// <summary>Returns business facts for this exact invocation; unknown external effects must remain unverified.</summary>
    ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken);
}
