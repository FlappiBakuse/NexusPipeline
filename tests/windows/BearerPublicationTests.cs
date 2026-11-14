using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class BearerPublicationTests
{
    private const string Token = "control_Publication_Continuation_Actual_032";

    [Fact(DisplayName = "Only The Admitted Request Continues Its Exact Owned Publication While New Authentication Remains Closed")]
    public async Task ContinuesOwnedPublication()
    {
        using var fixture = SecretTestRuntime.Create();
        using var verifier = await Create(fixture);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        var other = Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        using var request = proof.EnterRequest(); using var root = Root(fixture);
        using var continuation = Assert.IsType<ControlPublicationScope>(fixture.Service.BeginDocumentPublication(root, "fixture-extension"));
        var blocked = fixture.Index.BlockUnknown("secret_document_publication_in_progress");
        continuation.BindRecovery(blocked);
        proof.RequireCurrent();
        Assert.Throws<IOException>(() => verifier.Verify(Token));
        Assert.Throws<IOException>(() => other.RequireCurrent());
        proof.RequireCurrent();
        Task detached;
        using (ExecutionContext.SuppressFlow()) detached = Task.Run(() => Assert.Throws<IOException>(() => proof.RequireCurrent()));
        await detached; proof.RequireCurrent();
        continuation.Dispose(); Assert.Throws<IOException>(() => proof.RequireCurrent());
    }

    [Theory(DisplayName = "A Changed Recovery Generation Disposed Request Or Changed Credential Cannot Use An Owned Continuation")]
    [InlineData("generation")]
    [InlineData("request")]
    [InlineData("credential")]
    public async Task RejectsChangedAuthority(string changed)
    {
        using var fixture = SecretTestRuntime.Create();
        using var verifier = await Create(fixture);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        using var request = proof.EnterRequest(); using var root = Root(fixture);
        using var continuation = Assert.IsType<ControlPublicationScope>(fixture.Service.BeginDocumentPublication(root, "fixture-extension"));
        continuation.BindRecovery(fixture.Index.BlockUnknown("secret_document_publication_in_progress"));
        if (changed == "generation") fixture.Index.BlockUnknown("secret_document_publication_in_progress");
        else if (changed == "request") request.Dispose();
        if (changed == "credential")
        {
            using var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseConnections.Open(fixture.Database.DatabasePath, domains: Sqlite.DatabaseDomain.Runtime | Sqlite.DatabaseDomain.Configuration | Sqlite.DatabaseDomain.Records | Sqlite.DatabaseDomain.Notifications | Sqlite.DatabaseDomain.Extensions | Sqlite.DatabaseDomain.System | Sqlite.DatabaseDomain.Content);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE system.secret_fields SET revision=revision+1 WHERE owner_kind='host' AND field='control-bearer';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        if (changed == "credential") Assert.Throws<InvalidOperationException>(() => proof.RequireCurrent());
        else Assert.Throws<IOException>(() => proof.RequireCurrent());
    }

    [Fact(DisplayName = "A Publication Continuation Requires The Actual Resource Manager And Both Mutation Claims")]
    public async Task RequiresOwnedClaims()
    {
        using var fixture = SecretTestRuntime.Create();
        using var verifier = await Create(fixture);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        using var request = proof.EnterRequest();
        using var root = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("secrets:fields", ResourceClaimMode.Reference)]).Lease!;
        Assert.Throws<InvalidOperationException>(() => fixture.Service.BeginDocumentPublication(root, "fixture-extension"));
        fixture.Index.BlockUnknown("secret_document_publication_in_progress");
        Assert.Throws<IOException>(() => proof.RequireCurrent());
    }

    [Theory(DisplayName = "Matching Reason Text Or A Copied Recovery Record Cannot Grant Publication Continuation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnboundGeneration(bool copy)
    {
        using var fixture = SecretTestRuntime.Create();
        using var verifier = await Create(fixture);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        using var request = proof.EnterRequest(); using var root = Root(fixture);
        using var continuation = Assert.IsType<ControlPublicationScope>(fixture.Service.BeginDocumentPublication(root, "fixture-extension"));
        var blocked = fixture.Index.BlockUnknown(copy ? "secret_document_publication_in_progress" : "fixture_unknown_recovery");
        Assert.Throws<IOException>(() => continuation.BindRecovery(copy ? blocked with { } : blocked));
        Assert.Throws<IOException>(() => proof.RequireCurrent());
    }

    [Fact(DisplayName = "An Owned Secret Transaction Rechecks Authority Without Waiting Behind A Queue Item That Needs Its Sql Writer")]
    public async Task AvoidsWriterInversion()
    {
        using var fixture = SecretTestRuntime.Create();
        using var verifier = await Create(fixture);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(Token));
        using var request = proof.EnterRequest(); using var root = Root(fixture);
        using var continuation = Assert.IsType<ControlPublicationScope>(fixture.Service.BeginDocumentPublication(root, "fixture-extension"));
        continuation.BindRecovery(fixture.Index.BlockUnknown("secret_document_publication_in_progress"));
        using var writerEntered = new ManualResetEventSlim(); using var queueEntered = new ManualResetEventSlim();
        using var authorize = new ManualResetEventSlim();
        var writer = Task.Run(() => fixture.Database.Write((_, _) =>
        {
            writerEntered.Set();
            if (!authorize.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            proof.RequireCurrent(); return 1;
        }));
        Task<int>? queued = null;
        try
        {
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(10)));
            queued = Task.Run(() => fixture.Lease.PublishExecution(() =>
            {
                queueEntered.Set(); return fixture.Database.Write((_, _) => 2);
            }));
            Assert.True(queueEntered.Wait(TimeSpan.FromSeconds(10))); authorize.Set();
            Assert.Same(writer, await Task.WhenAny(writer, Task.Delay(1000)));
            Assert.Equal(1, await writer); Assert.Equal(2, await queued);
        }
        finally
        {
            authorize.Set();
            try { await Task.WhenAll(queued is null ? [writer] : [writer, queued]).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is IOException or TimeoutException) { }
        }
    }

    private static ResourceOperationLease Root(SecretTestRuntime fixture) => fixture.Resources.TryAcquire(Guid.NewGuid(),
        [new("extension-data:fixture-extension", ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation)]).Lease!;

    private static async Task<ControlBearerVerifier> Create(SecretTestRuntime fixture)
    {
        using var value = new SecretValue(Token);
        await fixture.Service.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId), 0, SecretDisposition.Set, value, default);
        return new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
    }
}
