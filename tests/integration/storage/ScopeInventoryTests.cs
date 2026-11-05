using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScopeInventoryTests
{
    [Fact(DisplayName = "Revocation Waits For Actual Registered Cleanup And Excludes Concurrent Activation")]
    public async Task WaitsForCleanup()
    {
        using var fixture = new BoundPackageFixture(); fixture.Publish();
        var owner = new ExtensionActivator(fixture.Catalog.Contracts, fixture.Catalog.Lease, fixture.Catalog.Resources);
        var cleanupEntered = NewSignal(); var finishCleanup = NewSignal();
        var scope = await owner.ActivateManagedAsync(fixture.ManifestBytes, "fixture-plugin", (_, _) =>
            new CallbackExtension(async lifetime => await lifetime.OwnAsync(new CallbackResource(async () =>
            { cleanupEntered.TrySetResult(); await finishCleanup.Task; }))), default);
        var operation = Guid.NewGuid();
        var reservation = owner.ReserveForApplyAsync(operation, "fixture-plugin", default).AsTask();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.False(scope.IsPublished); Assert.False(reservation.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await owner.ActivateManagedAsync(
                fixture.ManifestBytes, "fixture-plugin", (_, _) => new CallbackExtension(_ => ValueTask.CompletedTask), default));
        }
        finally { finishCleanup.TrySetResult(); }
        using var actual = await reservation;
        using var root = fixture.Catalog.Resources.TryAcquire(operation, [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease!;
        var facts = actual.Use(root, (activity, closure, activation) => (activity, closure, activation));
        Assert.Equal(ExtensionObservedActivity.Active, facts.activity); Assert.Equal(ExtensionScopeClosure.Stopped, facts.closure);
        Assert.Equal(scope.ActivationId, facts.activation);
        await scope.DisposeAsync();
    }

    [Fact(DisplayName = "An Activation Callback Still Running Cannot Be Reclassified As An Absent Scope")]
    public async Task KeepsRunningCallbacks()
    {
        using var fixture = new BoundPackageFixture();
        var owner = new ExtensionActivator(fixture.Catalog.Contracts, fixture.Catalog.Lease, fixture.Catalog.Resources);
        var callbackEntered = NewSignal(); var finishActivation = NewSignal();
        var activating = owner.ActivateManagedAsync(fixture.ManifestBytes, "fixture-plugin", (_, _) =>
            new CallbackExtension(async _ => { callbackEntered.TrySetResult(); await finishActivation.Task; }), default).AsTask();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = Guid.NewGuid(); var reservation = owner.ReserveForApplyAsync(operation, "fixture-plugin", default).AsTask();
        Assert.False(reservation.IsCompleted);
        finishActivation.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await activating);
        using var closed = await reservation;
        using var root = fixture.Catalog.Resources.TryAcquire(operation, [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Equal(ExtensionObservedActivity.Active, closed.Use(root, (activity, _, _) => activity));
    }

    [Fact(DisplayName = "A Real Cleanup Failure Does Not Produce A Closed Scope Reservation")]
    public async Task RejectsFailedCleanup()
    {
        using var fixture = new BoundPackageFixture();
        var owner = new ExtensionActivator(fixture.Catalog.Contracts, fixture.Catalog.Lease, fixture.Catalog.Resources);
        _ = await owner.ActivateManagedAsync(fixture.ManifestBytes, "fixture-plugin", (_, _) =>
            new CallbackExtension(async scope => await scope.OwnAsync(new CallbackResource(
                () => ValueTask.FromException(new IOException("controlled_cleanup_failure"))))), default);
        await Assert.ThrowsAsync<AggregateException>(async () =>
            await owner.ReserveForApplyAsync(Guid.NewGuid(), "fixture-plugin", default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await owner.ActivateManagedAsync(
            fixture.ManifestBytes, "fixture-plugin", (_, _) => new CallbackExtension(_ => ValueTask.CompletedTask), default));
    }

    [Fact(DisplayName = "Known Scope Closure Cannot Override An Independent Reference Or An Unrelated Root")]
    public async Task RespectsForeignReferences()
    {
        using var fixture = new BoundPackageFixture();
        var owner = new ExtensionActivator(fixture.Catalog.Contracts, fixture.Catalog.Lease, fixture.Catalog.Resources);
        var operation = Guid.NewGuid();
        using var pin = fixture.Catalog.Resources.TryAcquire(Guid.NewGuid(), [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease!;
        using var none = await owner.ReserveForApplyAsync(operation, "fixture-plugin", default);
        Assert.Null(fixture.Catalog.Resources.TryAcquire(operation, [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease);
        Assert.Throws<InvalidOperationException>(() => none.Use(pin, (_, _, _) => true));
        pin.Dispose();
        using var root = fixture.Catalog.Resources.TryAcquire(operation, [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease!;
        Assert.Equal(ExtensionObservedActivity.None, none.Use(root, (activity, _, _) => activity));
        none.Dispose();
        Assert.Throws<ObjectDisposedException>(() => none.Use(root, (_, _, _) => true));
    }

    [Fact(DisplayName = "Cancellation Before Reservation Preserves The Actual Published Scope")]
    public async Task KeepsCancelledReservation()
    {
        using var fixture = new BoundPackageFixture();
        var owner = new ExtensionActivator(fixture.Catalog.Contracts, fixture.Catalog.Lease, fixture.Catalog.Resources);
        await using var scope = await owner.ActivateManagedAsync(fixture.ManifestBytes, "fixture-plugin",
            (_, _) => new CallbackExtension(_ => ValueTask.CompletedTask), default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await owner.ReserveForApplyAsync(Guid.NewGuid(), "fixture-plugin", cancelled.Token));
        Assert.True(scope.IsPublished);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class CallbackExtension(Func<IExtensionScope, ValueTask> activate) : IExtension
    {
        public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken) => activate(scope);
    }
    private sealed class CallbackResource(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }
}
