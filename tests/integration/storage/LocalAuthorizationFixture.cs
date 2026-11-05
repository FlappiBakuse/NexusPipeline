using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class LocalAuthorizationFixture : IAsyncDisposable
{
    private readonly ExecutionSequentialFixture _source = new(configuration: true, mode: "tail", accept: false);
    internal ExecutionAdmissionFixture Base => _source.Base;
    internal PlanBuilderFixture Source => _source.Source;
    internal CompletionControlledClock Clock { get; } = new();
    internal RecordingPowerCalls Power { get; } = new();
    internal LocalCompletionAuthorization Authorization { get; }
    internal CompletionActionService Completion { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal AttemptAcceptanceResult Accepted { get; }
    private readonly NativeClientBinding? _binding;

    internal LocalAuthorizationFixture(bool local = true, bool nativeBinding = false)
    {
        try
        {
            var catalog = Source.Definitions.Read();
            var change = new AutomationChanges(catalog.Revision, [], [], [], [],
                [catalog.Queues.Single() with { CompletionAction = AutomationCompletionAction.Shutdown }], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "local-confirmation-fixture", change), change, DateTimeOffset.UtcNow);
            if (nativeBinding) _binding = new(Environment.ProcessId);
            var finality = new ExecutionRunFinality(Source.Instance, Source.Database, new());
            Authorization = new(Source.Instance, Source.Database, new(), Source.Lease, finality, _binding, Clock);
            Completion = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Lifecycle, Base.Resources,
                Base.Index, finality, Authorization, new RecordingHostExit(), Power, Clock);
            Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox, terminalObserver: Completion);
            var operation = Guid.NewGuid(); var request = Source.Request(new QueueTarget(Source.QueueId));
            Accepted = local ? Authorization.StartLocal(Base.Service, operation, "local-confirmation-fixture", request, DateTimeOffset.UtcNow)
                : Base.Service.Start(operation, "local-confirmation-fixture", request, DateTimeOffset.UtcNow);
        }
        catch { _binding?.Dispose(); _source.Dispose(); throw; }
    }

    internal async Task<AttemptReceipt> Run()
    {
        _ = Completion.RunAsync();
        var receipt = Assert.IsType<AttemptReceipt>(await Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(90)));
        await WaitUntil(() => Completion.PendingOperationId is not { } operation ||
            Completion.Read(operation)?.Phase == CompletionEffectPhase.Countdown);
        return receipt;
    }

    internal CompletionAuthorizationContext Context(AttemptReceipt receipt)
    {
        var source = CompletionIntentSource.ReadSaved(Source.Instance, Source.Database, new(),
            new ExecutionRunFinality(Source.Instance, Source.Database, new()), receipt)!;
        return CompletionAuthorizationContext.Create(CompletionActionPolicy.Combine([source.Intent]),
            [new(source.Intent, source.SubjectId)]);
    }

    internal async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (Completion.FailureCode is { } failure) throw new IOException(failure);
            Clock.Advance(TimeSpan.FromMilliseconds(20)); await Task.Delay(10);
        }
        Assert.True(condition()); Assert.Equal(0, Power.Calls);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Completion.StopAsync(); }
        finally
        {
            try { await Authorization.DisposeAsync(); }
            finally { _binding?.Dispose(); _source.Dispose(); }
        }
    }
}
