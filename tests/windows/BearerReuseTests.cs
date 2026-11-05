using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class BearerReuseTests
{
    [Fact]
    public async Task WaitsForPublication()
    {
        using var fixture = SecretTestRuntime.Create();
        const string token = "control_Actual_Publication_Proof_032";
        using var material = new SecretValue(token);
        await fixture.Service.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId),
            0, SecretDisposition.Set, material, default);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(token));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var publication = Task.Run(() => fixture.Lease.PublishExecution(() =>
        {
            var blocked = fixture.Index.BlockUnknown("recovery_verification_in_progress");
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            fixture.Index.PublishVerified(blocked.Revision, blocked.Revision + 1, []);
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var authorization = Task.Run(() => { proof.RequireCurrent(); return verifier.Verify(token); });
        try { Assert.NotSame(authorization, await Task.WhenAny(authorization, Task.Delay(50))); }
        finally { release.Set(); }
        await publication;
        Assert.IsType<VerifiedControlBearer>(await authorization);
        fixture.Index.BlockUnknown("fixture_unknown_recovery");
        Assert.Throws<IOException>(() => proof.RequireCurrent());
        var blocked = fixture.Index.Current.Revision;
        fixture.Index.PublishVerified(blocked, blocked + 1,
            [new("execution:fixture", Guid.NewGuid(), "recovery_pending")]);
        Assert.Throws<IOException>(() => proof.RequireCurrent());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ReusesProducerEvidence(int clients)
    {
        using var fixture = SecretTestRuntime.Create();
        const string token = "control_Actual_Shared_Producer_Proof_032";
        var key = SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId);
        using var material = new SecretValue(token);
        var saved = await fixture.Service.ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, material, default);
        var reads = 0;
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service)
            { ProducerRead = () => reads++ };
        var proofs = Enumerable.Range(0, clients).Select(_ => Assert.IsType<VerifiedControlBearer>(verifier.Verify(token))).ToArray();
        for (var tick = 0; tick < 10; tick++) foreach (var proof in proofs) proof.RequireCurrent();
        Assert.Equal(1, reads);
        fixture.Index.BlockUnknown("fixture_recovery_changed");
        foreach (var proof in proofs) Assert.Throws<IOException>(() => proof.RequireCurrent());
        var revision = fixture.Index.Current.Revision;
        fixture.Index.PublishVerified(revision, revision + 1, []);
        Assert.IsType<VerifiedControlBearer>(verifier.Verify(token)).RequireCurrent();
        Assert.Equal(2, reads);
        await fixture.Service.ApplyAsync(Guid.NewGuid(), key, saved.Revision, SecretDisposition.Clear, null, default);
        foreach (var proof in proofs) Assert.Throws<InvalidOperationException>(() => proof.RequireCurrent());
        Assert.Null(verifier.Verify(token));
        Console.WriteLine($"clients={clients}; authorizationTicks=10; producerReadsBeforeInvalidation=1");
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("proof")]
    public async Task RejectsProducerChanges(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        const string token = "control_Actual_Changed_Producer_Proof_032";
        var key = SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId);
        var operation = Guid.NewGuid();
        using var material = new SecretValue(token);
        await fixture.Service.ApplyAsync(operation, key, 0, SecretDisposition.Set, material, default);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(token));
        using (var connection = DatabaseConnections.Open(fixture.Database.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = fault == "payload"
                ? "UPDATE secret_payloads SET digest=$value WHERE preparation_operation_id=$operation;"
                : "UPDATE secret_preparations SET replay_proof=$value WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$value", new string('0', 64));
            command.Parameters.AddWithValue("$operation", operation.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.ThrowsAny<IOException>(() => proof.RequireCurrent());
        Assert.ThrowsAny<IOException>(() => verifier.Verify(token));
    }
}
