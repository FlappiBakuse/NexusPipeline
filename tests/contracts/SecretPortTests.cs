using System.Text;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SecretPortTests
{
    [Fact(DisplayName = "Dedicated Secret Port Converts Explicit Semantics And Releases Transferred Plaintext")]
    public async Task TransfersOwnedPlaintext()
    {
        var store = new Store();
        var policy = new Policy();
        var (scope, port) = await Activate(store, policy);
        await using var lifetime = scope;
        var subject = policy.Subject;
        var operation = Guid.NewGuid();
        var keep = await port.ApplyAsync(operation, subject, "cookie", 3, SecretChange.Keep(), default);
        Assert.Equal(SecretDisposition.Keep, store.LastDisposition);
        Assert.Null(store.LastMaterial);
        Assert.Equal(operation, store.LastOperation);
        Assert.Equal(3, store.LastRevision);
        Assert.Equal("fixture", store.LastKey!.ExtensionId);
        Assert.Equal(subject, store.LastKey.SubjectId);
        Assert.True(keep.IsConfigured);
        var draft = new SecretDraft("fixture-only-token");
        await port.ApplyAsync(Guid.NewGuid(), subject, "cookie", 3, SecretChange.Set(draft), default);
        Assert.Equal(SecretDisposition.Set, store.LastDisposition);
        Assert.True(store.MaterialMatchedFixture);
        Assert.Throws<ObjectDisposedException>(() => draft.Use(_ => throw new IOException()));
        Assert.Throws<ObjectDisposedException>(() => store.LastMaterial!.Use(_ => throw new IOException()));
        var cleared = await port.ApplyAsync(Guid.NewGuid(), subject, "cookie", 3, SecretChange.Clear(), default);
        Assert.Equal(SecretDisposition.Clear, store.LastDisposition);
        Assert.Null(store.LastMaterial);
        Assert.False(cleared.IsConfigured);
        Assert.Null(cleared.SecretId);
        Assert.All(policy.ObservedScopes, value => Assert.Equal(SecretSubjectScopes.SelectedBinding, value));
    }

    [Theory(DisplayName = "Rejected Secret Changes Never Reach Storage And Their Draft Is Still Disposed")]
    [InlineData("permission")]
    [InlineData("subject")]
    [InlineData("disposed")]
    [InlineData("operation")]
    public async Task DisposesRejectedChanges(string fault)
    {
        var store = new Store();
        var policy = new Policy();
        var (scope, port) = await Activate(store, policy, declared: fault != "permission");
        await using var lifetime = scope;
        if (fault == "disposed") await scope.DisposeAsync();
        var draft = new SecretDraft("fixture-only-token");
        var operation = fault == "operation" ? Guid.Empty : Guid.NewGuid();
        var subject = fault == "subject" ? Guid.NewGuid() : policy.Subject;
        Assert.NotNull(await Record.ExceptionAsync(() => port.ApplyAsync(operation, subject, "cookie", 3, SecretChange.Set(draft), default).AsTask()));
        Assert.Equal(0, store.Calls);
        Assert.Throws<ObjectDisposedException>(() => draft.Use(_ => throw new IOException()));
    }

    [Theory(DisplayName = "Late Secret Reads Cannot Escape Revoked Authorization Or Activation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokesLateReads(bool dispose)
    {
        var store = new Store { DelayRead = true };
        var policy = new Policy();
        var (scope, port) = await Activate(store, policy);
        await using var lifetime = scope;
        var read = port.ReadAsync(policy.Subject, "cookie", default).AsTask();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) await scope.DisposeAsync();
        else policy.ReadAllowed = false;
        var material = new SecretValue("fixture-only-token");
        store.Release.SetResult(material);
        Assert.NotNull(await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Throws<ObjectDisposedException>(() => material.Use(_ => throw new IOException()));
    }

    [Fact(DisplayName = "Secret Read Permission Is Independent And Unavailable Storage Never Becomes An Empty Draft")]
    public async Task SeparatesReadPermission()
    {
        var store = new Store();
        var policy = new Policy { ReadAllowed = false };
        var (scope, port) = await Activate(store, policy);
        await using var lifetime = scope;
        var state = await port.GetStateAsync(policy.Subject, "cookie", default);
        Assert.True(state.IsConfigured);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => port.ReadAsync(policy.Subject, "cookie", default).AsTask());
        Assert.False(store.Entered.Task.IsCompleted);
        policy.ReadAllowed = true;
        store.Unavailable = true;
        var failure = await Assert.ThrowsAsync<IOException>(() => port.ReadAsync(policy.Subject, "cookie", default).AsTask());
        Assert.Equal("secret_unavailable", failure.Message);
        Assert.True((await port.GetStateAsync(policy.Subject, "cookie", default)).IsConfigured);
    }

    [Fact(DisplayName = "Storage Write Failure Clears Both Boundary Buffers And Propagates Failure")]
    public async Task ClearsFailedWrites()
    {
        var store = new Store { FailWrite = true };
        var policy = new Policy();
        var (scope, port) = await Activate(store, policy);
        await using var lifetime = scope;
        var draft = new SecretDraft("fixture-only-token");
        await Assert.ThrowsAsync<IOException>(() => port.ApplyAsync(Guid.NewGuid(), policy.Subject, "cookie", 3, SecretChange.Set(draft), default).AsTask());
        Assert.Throws<ObjectDisposedException>(() => draft.Use(_ => throw new IOException()));
        Assert.Throws<ObjectDisposedException>(() => store.LastMaterial!.Use(_ => throw new IOException()));
    }

    private static async Task<(ExtensionScope Scope, ExtensionSecretPort Port)> Activate(Store store, Policy policy, bool declared = true)
    {
        var bytes = Encoding.UTF8.GetBytes($$$"""
            {"format":"nexus.extension.manifest","version":1,"extensionId":"fixture","artifactName":"Fixture",
            "displayName":"Fixture","packageVersion":"0.5.0","kind":"managed-code","minHostVersion":"0.18.0",
            "sdkVersion":"0.1.0","contractFingerprint":"{{{SdkIdentity.ContractFingerprint}}}","capabilities":[],
            "permissions":{{{(declared ? "{\"secretAccess\":[\"selected-binding\"]}" : "{}")}}},
            "managed":{"entryAssembly":"Fixture.dll","entryType":"Fixture.Extension"}}
            """);
        var reader = new ContractReader();
        ExtensionSecretPort? port = null;
        var scope = await new ExtensionActivator(reader).ActivateManagedAsync(bytes, "fixture", (manifest, lifetime) =>
        {
            port = new(manifest, lifetime, policy, store, reader);
            return new EmptyExtension();
        }, default);
        return (scope, port!);
    }

    private sealed class EmptyExtension : IExtension
    {
        public ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class Policy : ISecretAccessPolicy
    {
        public Guid Subject { get; } = Guid.NewGuid();
        public bool ReadAllowed { get; set; } = true;
        public List<SecretSubjectScopes> ObservedScopes { get; } = [];
        public void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes scopes)
        {
            ObservedScopes.Add(scopes);
            if (key.SubjectId != Subject || key.Field != "cookie" || key.ExtensionId != "fixture" || access == SecretAccessKind.Read && !ReadAllowed)
                throw new UnauthorizedAccessException("secret_access_denied");
        }
    }

    private sealed class Store : ISecretFieldStore
    {
        private readonly Guid _secretId = Guid.NewGuid();
        public int Calls { get; private set; }
        public SecretDisposition? LastDisposition { get; private set; }
        public SecretFieldKey? LastKey { get; private set; }
        public SecretValue? LastMaterial { get; private set; }
        public Guid LastOperation { get; private set; }
        public long LastRevision { get; private set; }
        public bool MaterialMatchedFixture { get; private set; }
        public bool DelayRead { get; init; }
        public bool Unavailable { get; set; }
        public bool FailWrite { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SecretValue> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SecretFieldStatus> GetStateAsync(SecretFieldKey key, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new SecretFieldStatus(3, _secretId, true, !Unavailable));
        }
        public ValueTask<SecretFieldStatus> ApplyAsync(Guid operationId, SecretFieldKey key, long expectedRevision,
            SecretDisposition disposition, SecretValue? value, CancellationToken cancellationToken)
        {
            Calls++;
            LastKey = key; LastOperation = operationId; LastRevision = expectedRevision; LastDisposition = disposition; LastMaterial = value;
            value?.Use(material => MaterialMatchedFixture = material.SequenceEqual("fixture-only-token"));
            if (FailWrite) throw new IOException("controlled_secret_write_failure");
            return ValueTask.FromResult(disposition == SecretDisposition.Clear ? new SecretFieldStatus(4, null, false, false) : new(3, _secretId, true, true));
        }
        public async ValueTask<SecretValue> ReadAsync(SecretFieldKey key, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            if (Unavailable) throw new IOException("secret_unavailable");
            return DelayRead ? await Release.Task : new SecretValue("fixture-only-token");
        }
    }
}
