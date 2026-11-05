using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ControlTokenTests
{
    private const string Token = "control_Local_Confirmation_Dpapi_032";

    [Fact(DisplayName = "Local Token Changes Wait For The Server Confirmation And Reuse Only The Exact Durable Secret Request")]
    public async Task ConfirmsTokenChanges()
    {
        using var fixture = SecretTestRuntime.Create();
        var dialog = new ControlledDialog();
        await using var service = new ControlTokenService(fixture.Instance, fixture.Database, fixture.Lease,
            fixture.Resources, fixture.Service, null, dialog);
        using var value = new SecretValue(Token);
        var operation = Guid.NewGuid();
        var pending = service.ChangeAsync(operation, 0, SecretDisposition.Set, value);
        await dialog.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await fixture.Service.GetStateAsync(SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId), default)).IsConfigured);
        Assert.Same(pending, service.ChangeAsync(operation, 0, SecretDisposition.Set, value));
        Assert.Throws<OperationConflictException>(() => { _ = service.ChangeAsync(operation, 1, SecretDisposition.Set, value); });
        dialog.Answer.TrySetResult(true);
        var saved = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(saved.IsConfigured);
        Assert.Equal(saved, await service.ChangeAsync(operation, 0, SecretDisposition.Set, value));
        Assert.Equal(1, dialog.Calls);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        Assert.Null(fixture.Service.ReadHostOperation(operation, "proxy-password"));
    }

    [Fact(DisplayName = "Cancelling An Actual Pending Confirmation Does Not Register A Secret Mutation Or Credential")]
    public async Task CancelsPendingConfirmation()
    {
        using var fixture = SecretTestRuntime.Create();
        var dialog = new ControlledDialog();
        await using var service = new ControlTokenService(fixture.Instance, fixture.Database, fixture.Lease,
            fixture.Resources, fixture.Service, null, dialog);
        using var value = new SecretValue(Token);
        var operation = Guid.NewGuid();
        var work = service.ChangeAsync(operation, 0, SecretDisposition.Set, value);
        await dialog.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(service.Cancel(operation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await work);
        var state = await fixture.Service.GetStateAsync(SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId), default);
        Assert.Equal(0, state.Revision); Assert.False(state.IsConfigured);
        Assert.Null(new NexusPipeline.Platform.Storage.Operations.OperationQueryService(fixture.Instance, fixture.Lease, fixture.Database)
            .Read(operation, "host/" + fixture.Instance.InstanceId.ToString("D") + "/control-bearer"));
    }

    [Fact]
    public async Task QueriesProxyOperation()
    {
        using var fixture = SecretTestRuntime.Create();
        var key = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var secret = new SecretValue("Synthetic-proxy-operation");
        var operationId = Guid.NewGuid();
        var saved = await fixture.Service.ApplyAsync(operationId, key, 0, SecretDisposition.Set, secret, default);
        var result = fixture.Service.ReadHostOperation(operationId, "proxy-password");
        Assert.NotNull(result); Assert.Equal(operationId, result.OperationId);
        Assert.Equal("secret.change", result.Kind); Assert.Equal("completed", result.Status);
        Assert.Equal(1, saved.Revision);
        Assert.Null(fixture.Service.ReadHostOperation(Guid.NewGuid(), "proxy-password"));
        Assert.Throws<ArgumentException>(() => fixture.Service.ReadHostOperation(operationId, "control-bearer"));
        var cleared = await fixture.Service.ApplyAsync(Guid.NewGuid(), key, saved.Revision, SecretDisposition.Clear, null, default);
        Assert.False(cleared.IsConfigured);
        Assert.Equal("completed", fixture.Service.ReadHostOperation(operationId, "proxy-password")!.Status);
    }

    private sealed class ControlledDialog : ILocalConfirmationDialog
    {
        internal TaskCompletionSource<LocalConfirmationPrompt> Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        public Task<bool> ShowAsync(LocalConfirmationPrompt prompt, TimeSpan timeout, CancellationToken cancellation)
        { Calls++; Seen.TrySetResult(prompt); return Answer.Task.WaitAsync(cancellation); }
    }
}
