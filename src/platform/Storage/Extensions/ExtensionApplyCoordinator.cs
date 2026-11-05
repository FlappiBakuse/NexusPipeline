using System.Runtime.Versioning;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ExtensionApplyFlight(OperationIdentity identity)
{
    internal OperationIdentity Identity { get; } = identity;
    internal TaskCompletionSource<PayloadAcceptanceResult> Acceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<ExtensionApplyCompletion> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? Running;
    internal Task<PayloadAcceptanceResult> WaitAcceptedAsync(CancellationToken clientCancellation) =>
        Acceptance.Task.WaitAsync(clientCancellation);
}

[SupportedOSPlatform("windows")]
internal sealed class ExtensionApplyCoordinator(ExtensionApplyWorkflow workflow)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ExtensionApplyFlight> _flights = [];
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private bool _stopping;

    internal ExtensionApplyFlight Start(Guid operationId, string subject, ExtensionApplyRequest request)
    {
        var identity = request.Identity(operationId, subject);
        var replay = workflow.ReadAccepted(operationId, subject, request);
        lock (_gate)
        {
            if (_flights.TryGetValue(operationId, out var running))
            {
                if (running.Identity != identity) throw new OperationConflictException();
                return running;
            }
            var flight = new ExtensionApplyFlight(identity);
            Observe(flight.Acceptance.Task); Observe(flight.Completion.Task);
            if (replay is not null)
            {
                flight.Acceptance.SetResult(replay); flight.Completion.SetResult(new(replay, null, true));
                return flight;
            }
            if (_stopping) throw new IOException("extension_apply_coordinator_stopping");
            if (_flights.Count >= 32) throw new IOException("extension_apply_queue_full");
            _flights.Add(operationId, flight);
            flight.Running = Task.Run(() => RunAsync(flight, subject, request), CancellationToken.None);
            return flight;
        }
    }

    private async Task RunAsync(ExtensionApplyFlight flight, string subject, ExtensionApplyRequest request)
    {
        var entered = false;
        try
        {
            await _execution.WaitAsync(_stop.Token).ConfigureAwait(false); entered = true;
            var result = await workflow.RunAsync(flight.Identity.OperationId, subject, request,
                acceptance => flight.Acceptance.TrySetResult(acceptance), _stop.Token).ConfigureAwait(false);
            flight.Completion.TrySetResult(result);
        }
        catch (Exception failure)
        {
            flight.Acceptance.TrySetException(failure); flight.Completion.TrySetException(failure);
        }
        finally
        {
            if (entered) _execution.Release();
            lock (_gate) _flights.Remove(flight.Identity.OperationId);
        }
    }

    internal async Task StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            tasks = _flights.Values.Select(item => item.Running ?? throw new IOException("extension_apply_task_unbound")).ToArray();
        }
        _stop.Cancel();
        await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
    }

    private static void Observe(Task task) => _ = task.ContinueWith(static failed => { _ = failed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
