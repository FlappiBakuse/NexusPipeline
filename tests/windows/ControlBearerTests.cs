using System.Text;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ControlBearerTests
{
    private const string First = "control_Bearer_First_Actual_Dpapi_032";
    private const string Second = "control_Bearer_Second_Actual_Dpapi_032";

    [Fact(DisplayName = "Actual Host Bearer Ciphertext Verifies Only Its Exact Current Revision And Cannot Be Read Through The Generic Port")]
    public async Task BindsCurrentBearer()
    {
        using var fixture = SecretTestRuntime.Create();
        var key = SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId);
        using var material = new SecretValue(First);
        var first = await fixture.Service.ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, material, default);
        SecretFieldStore.VerifyInventory(fixture.Database);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(First));
        proof.RequireCurrent();
        Assert.Null(verifier.Verify(Second)); Assert.Null(verifier.Verify("short"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fixture.Service.ReadAsync(key, default));
        using var next = new SecretValue(Second);
        var second = await fixture.Service.ApplyAsync(Guid.NewGuid(), key, first.Revision, SecretDisposition.Set, next, default);
        Assert.Equal(first.Revision + 1, second.Revision);
        Assert.Null(verifier.Verify(First));
        Assert.Throws<InvalidOperationException>(() => proof.RequireCurrent());
        Assert.IsType<VerifiedControlBearer>(verifier.Verify(Second)).RequireCurrent();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets"), "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(First)) >= 0);
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Second)) >= 0);
        }
    }

    [Theory(DisplayName = "Fresh Bearer Authentication Rejects Changed Sql Field Or Producer Identity Even With Real Decryptable Payloads")]
    [InlineData("foreignPayload")]
    [InlineData("producerSubject")]
    public async Task RejectsChangedProducers(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        var key = SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId);
        var operation = Guid.NewGuid();
        using var first = new SecretValue(First); using var second = new SecretValue(Second);
        await fixture.Service.ApplyAsync(operation, key, 0, SecretDisposition.Set, first, default);
        var foreign = await fixture.Service.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId),
            0, SecretDisposition.Set, second, default);
        using (var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Notifications | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = fault == "foreignPayload"
                ? "UPDATE system.secret_fields SET secret_id=NULL WHERE owner_kind='host' AND field='proxy-password'; " +
                  "UPDATE system.secret_fields SET secret_id=$id WHERE owner_kind='host' AND field='control-bearer';"
                : "UPDATE main.operations SET subject_id=$subject WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$id", foreign.SecretId!.Value.ToString("D"));
            command.Parameters.AddWithValue("$subject", "host/" + fixture.Instance.InstanceId.ToString("D") + "/proxy-password");
            command.Parameters.AddWithValue("$operation", operation.ToString("D"));
            command.ExecuteNonQuery();
        }
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        Assert.Throws<IOException>(() => verifier.Verify(fault == "foreignPayload" ? Second : First));
    }

    [Theory(DisplayName = "Missing Or Changed Real Bearer Payload Cannot Reuse An Earlier Opaque Verification")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsChangedPayload(bool missing)
    {
        using var fixture = SecretTestRuntime.Create();
        var key = SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId);
        using var material = new SecretValue(First);
        var saved = await fixture.Service.ApplyAsync(Guid.NewGuid(), key, 0, SecretDisposition.Set, material, default);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(First));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        var original = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
        verifier.Dispose();
        Assert.Throws<ObjectDisposedException>(() => proof.RequireCurrent());
        using var reopened = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        if (missing) File.Delete(path);
        else { var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(path, changed); }
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Assert.ThrowsAny<IOException>(() => reopened.Verify(First));
        var status = await fixture.Service.GetStateAsync(key, default);
        Assert.Equal(saved.SecretId, status.SecretId); Assert.True(status.IsConfigured); Assert.False(status.IsReadable);
        if (before is null) Assert.False(File.Exists(path)); else Assert.Equal(before, File.ReadAllBytes(path));
    }
}
