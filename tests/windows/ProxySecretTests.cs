using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ProxySecretTests
{
    private const string Canary = "host-proxy-dpapi-fixture-canary-018";

    [Fact(DisplayName = "Host And Extension Fields Use Distinct Cipher References And Exact Operation Replay")]
    public async Task SeparatesCredentialOwners()
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        var extension = new SecretFieldKey("host", fixture.Instance.InstanceId, "proxy-password");
        using var material = new SecretValue(Canary);
        using var extensionMaterial = new SecretValue("extension-fixture-different-value");
        var operation = Guid.NewGuid();
        var state = await fixture.Service.ApplyAsync(operation, host, 0, SecretDisposition.Set, material, default);
        var other = await fixture.Service.ApplyAsync(Guid.NewGuid(), extension, 0, SecretDisposition.Set, extensionMaterial, default);
        Assert.NotEqual(state.SecretId, other.SecretId);
        await Assert.ThrowsAsync<OperationConflictException>(async () =>
            await fixture.Service.ApplyAsync(operation, extension, 0, SecretDisposition.Set, material, default));
        using (var root = Root(fixture, out var owner))
        using (var credential = fixture.Service.PrepareProxyCredential(root, owner))
        {
            credential.VerifyHeld(fixture.Instance.InstanceId, owner);
            Assert.Equal(new SecretFieldReference(state.Revision, state.SecretId), credential.Reference);
            credential.Use(value => Assert.True(value.SequenceEqual(Canary)));
        }
        await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 1, SecretDisposition.Clear, null, default);
        Assert.Equal(state, await fixture.Service.ApplyAsync(operation, host, 0, SecretDisposition.Set, material, default));
        using var extensionRead = await fixture.Service.ReadAsync(extension, default);
        extensionRead.Use(value => Assert.True(value.SequenceEqual("extension-fixture-different-value")));
        AssertNoPlaintext(fixture);
    }

    [Fact(DisplayName = "A Prepared Reference Retains The Actual Global Claim After Root Dispose And Blocks Replacement")]
    public async Task RetainsCredentialClaims()
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var value = new SecretValue(Canary);
        var first = await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, value, default);
        var root = Root(fixture, out var owner);
        using var credential = fixture.Service.PrepareProxyCredential(root, owner);
        root.Dispose();
        credential.Use(secret => Assert.True(secret.SequenceEqual(Canary)));
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 1, SecretDisposition.Clear, null, default));
        Assert.Equal("secret_resource_conflict", conflict.Message);
        Assert.Equal(first, await fixture.Service.GetStateAsync(host, default));
        credential.Dispose();
        Assert.Throws<ObjectDisposedException>(() => credential.Use(_ => { }));
        var cleared = await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 1, SecretDisposition.Clear, null, default);
        Assert.Equal(2, cleared.Revision);
        Assert.False(cleared.IsConfigured);
    }

    [Theory(DisplayName = "Host Preparation Requires The Actual Root Before Reading Any Credential")]
    [InlineData("foreign-manager")]
    [InlineData("wrong-operation")]
    [InlineData("missing-claim")]
    [InlineData("disposed-root")]
    [InlineData("expired-instance")]
    public void RequiresActualRoot(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        var manager = fault == "foreign-manager" ? new ResourceLeaseManager(fixture.Lifecycle) : fixture.Resources;
        var owner = Guid.NewGuid();
        using var root = manager.TryAcquire(owner,
            [new(fault == "missing-claim" ? "settings:host" : "secrets:fields", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("fixture_root_unavailable");
        if (fault == "disposed-root") root.Dispose();
        if (fault == "expired-instance") fixture.Lease.Dispose();
        var before = PersistentFingerprints(fixture);
        var error = Record.Exception(() => fixture.Service.PrepareProxyCredential(root,
            fault == "wrong-operation" ? Guid.NewGuid() : owner));
        Assert.NotNull(error);
        Assert.True(error is ObjectDisposedException or InvalidOperationException or IOException);
        Assert.Equal(before, PersistentFingerprints(fixture));
    }

    [Theory(DisplayName = "A Prepared Reference Cannot Follow A Changed Revision Or Another Real Ciphertext")]
    [InlineData("revision")]
    [InlineData("secret-id")]
    public async Task BindsCredentialRevision(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var material = new SecretValue(Canary);
        var state = await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, material, default);
        var alternate = await fixture.Service.ApplyAsync(Guid.NewGuid(), fixture.Key, 0, SecretDisposition.Set, material, default);
        using var root = Root(fixture, out var owner);
        using var credential = fixture.Service.PrepareProxyCredential(root, owner);
        Execute(fixture, fault == "revision"
            ? "UPDATE secret_fields SET revision=revision+1 WHERE owner_kind='host';"
            : "UPDATE secret_fields SET secret_id=NULL WHERE owner_kind='extension'; " +
                "UPDATE secret_fields SET secret_id=$id WHERE owner_kind='host';", alternate.SecretId?.ToString("D"));
        var before = PersistentFingerprints(fixture);
        Assert.Equal("secret_revision_conflict", Assert.Throws<InvalidOperationException>(() => credential.Use(_ =>
            throw new Exception("stale_credential_callback_reached"))).Message);
        Assert.Equal(new SecretFieldReference(state.Revision, state.SecretId), credential.Reference);
        Assert.Equal(before, PersistentFingerprints(fixture));
    }

    [Theory(DisplayName = "Configured Unreadable Host Credentials Remain Configured And Never Become An Empty Fallback")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesUnreadableCredentials(bool missing)
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var material = new SecretValue(Canary);
        var state = await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, material, default);
        var payload = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        if (missing) File.Delete(payload);
        else { var bytes = File.ReadAllBytes(payload); bytes[0] ^= 1; File.WriteAllBytes(payload, bytes); }
        using var root = Root(fixture, out var owner);
        var before = PersistentFingerprints(fixture);
        Assert.Equal("secret_unavailable", Assert.Throws<IOException>(() =>
            fixture.Service.PrepareProxyCredential(root, owner)).Message);
        var current = await fixture.Service.GetStateAsync(host, default);
        Assert.Equal(state.SecretId, current.SecretId);
        Assert.Equal(state.Revision, current.Revision);
        Assert.True(current.IsConfigured);
        Assert.False(current.IsReadable);
        Assert.Equal(before, PersistentFingerprints(fixture));
    }

    [Theory(DisplayName = "Frozen Host Secret Plans Reject Unknown Or Cross Namespace Metadata Before Acceptance")]
    [InlineData("missing-owner")]
    [InlineData("unknown-owner")]
    [InlineData("host-extension")]
    [InlineData("host-field")]
    [InlineData("host-instance")]
    public void RejectsUnknownNamespaces(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var value = new SecretValue(Canary);
        var codec = new PreparationPlanCodec(fixture.Instance, new(), fixture.Database);
        using var prepared = codec.Freeze(SecretFieldStore.Identity(Guid.NewGuid(), host, 0, SecretDisposition.Set),
            Guid.NewGuid(), host, 0, value);
        var saved = prepared.Plan.Export();
        var changed = JsonNode.Parse(saved)!.AsObject();
        Assert.Equal("host", changed["ownerKind"]!.GetValue<string>());
        Assert.False(changed.ContainsKey("extensionId"));
        switch (fault)
        {
            case "missing-owner": changed.Remove("ownerKind"); break;
            case "unknown-owner": changed["ownerKind"] = "system"; break;
            case "host-extension": changed["extensionId"] = "host"; break;
            case "host-field": changed["field"] = "arbitrary"; break;
            case "host-instance": changed["subjectId"] = Guid.NewGuid().ToString("D"); break;
        }
        var before = PersistentFingerprints(fixture);
        var error = Record.Exception(() => codec.Decode(Encoding.UTF8.GetBytes(changed.ToJsonString())));
        Assert.NotNull(error);
        Assert.True(error is IOException or JsonException);
        Assert.Equal(saved, prepared.Plan.Export());
        Assert.Equal(before, PersistentFingerprints(fixture));
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        AssertNoPlaintext(fixture);
    }

    [Theory(DisplayName = "Startup Diagnoses Host Namespace Damage Without Repairing Source Bytes")]
    [InlineData("foreign-instance")]
    [InlineData("unknown-owner")]
    [InlineData("wrong-field")]
    public async Task PreservesDamagedNamespaces(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        using var value = new SecretValue(Canary);
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, value, default);
        var foreign = Guid.NewGuid().ToString("D");
        Execute(fixture, fault switch
        {
            "foreign-instance" => "UPDATE secret_fields SET owner_id=$id,subject_id=$id WHERE owner_kind='host';",
            "unknown-owner" => "PRAGMA ignore_check_constraints=ON; UPDATE secret_fields SET owner_kind='system' WHERE owner_kind='host';",
            _ => "PRAGMA ignore_check_constraints=ON; UPDATE secret_fields SET field='arbitrary' WHERE owner_kind='host';"
        }, foreign);
        var before = PersistentFingerprints(fixture);
        var diagnostics = Path.Combine(Path.GetDirectoryName(fixture.Instance.RuntimeRoot)!, "namespace-diagnostics");
        Assert.ThrowsAny<IOException>(() => StateDatabase.OpenExisting(fixture.Instance, diagnostics,
            new JournalStartupAdmission(fixture.Instance, new())));
        Assert.Equal(before, PersistentFingerprints(fixture));
    }

    [Fact(DisplayName = "Reopened Dpapi Host State Uses The Same Namespace And Forbids Unbound Plaintext Reads")]
    public async Task BindsReopenedSecrets()
    {
        using var fixture = SecretTestRuntime.Create();
        using var material = new SecretValue(Canary);
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        var state = await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, material, default);
        var diagnostics = Path.Combine(Path.GetDirectoryName(fixture.Instance.RuntimeRoot)!, "valid-diagnostics");
        var reopened = StateDatabase.OpenExisting(fixture.Instance, diagnostics, new JournalStartupAdmission(fixture.Instance, new()));
        var service = new SecretFieldService(fixture.Instance, reopened, fixture.Lease, fixture.Resources, fixture.Index, new());
        using var root = Root(fixture, out var owner);
        using var prepared = service.PrepareProxyCredential(root, owner);
        Assert.Equal(new SecretFieldReference(state.Revision, state.SecretId), prepared.Reference);
        prepared.Use(secret => Assert.True(secret.SequenceEqual(Canary)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { using var ignored = await service.ReadAsync(host, default); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { using var ignored = await fixture.Store.ReadAsync(host, default); });
        AssertNoPlaintext(fixture);
    }

    [Fact(DisplayName = "An Unconfigured Host Reference Still Prevents A Concurrent First Password Publication")]
    public async Task HoldsUnconfiguredReference()
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var root = Root(fixture, out var owner);
        using var prepared = fixture.Service.PrepareProxyCredential(root, owner);
        Assert.False(prepared.IsConfigured);
        Assert.Equal(new SecretFieldReference(0, null), prepared.Reference);
        Assert.Equal("secret_not_configured", Assert.Throws<IOException>(() => prepared.Use(_ =>
            throw new Exception("unconfigured_callback_reached"))).Message);
        using var value = new SecretValue(Canary);
        Assert.Equal("secret_resource_conflict", (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, value, default))).Message);
        Assert.False((await fixture.Service.GetStateAsync(host, default)).IsConfigured);
    }

    [Fact(DisplayName = "A Failed Host Publication Rolls Back The Original Operation And Retains Its Real Ciphertext")]
    public async Task PreservesFailedPublication()
    {
        using var fixture = SecretTestRuntime.Create();
        var host = SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId);
        using var material = new SecretValue(Canary);
        var state = await fixture.Service.ApplyAsync(Guid.NewGuid(), host, 0, SecretDisposition.Set, material, default);
        var payload = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets/payloads")));
        var ciphertext = File.ReadAllBytes(payload);
        var operation = Guid.NewGuid();
        Execute(fixture, "CREATE TRIGGER fail_host_publication BEFORE UPDATE ON secret_fields WHEN NEW.owner_kind='host' " +
            "BEGIN SELECT RAISE(ABORT,'fixture_host_field_rollback'); END;");
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await fixture.Service.ApplyAsync(operation, host, 1, SecretDisposition.Clear, null, default));
        Assert.Equal(state, await fixture.Service.GetStateAsync(host, default));
        Assert.Equal(ciphertext, File.ReadAllBytes(payload));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM operations WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", operation.ToString("D"));
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
        Execute(fixture, "DROP TRIGGER fail_host_publication;");
        using var root = Root(fixture, out var owner);
        using var prepared = fixture.Service.PrepareProxyCredential(root, owner);
        prepared.Use(secret => Assert.True(secret.SequenceEqual(Canary)));
    }

    private static ResourceOperationLease Root(SecretTestRuntime fixture, out Guid owner)
    {
        owner = Guid.NewGuid();
        return fixture.Resources.TryAcquire(owner, [new("secrets:fields", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("fixture_root_unavailable");
    }

    private static void Execute(SecretTestRuntime fixture, string sql, string? id = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Database.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string[] PersistentFingerprints(SecretTestRuntime fixture) =>
        Directory.EnumerateFiles(fixture.Instance.RuntimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" && !path.EndsWith("-shm", StringComparison.Ordinal) &&
                (!path.EndsWith("-wal", StringComparison.Ordinal) || new FileInfo(path).Length != 0))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture.Instance.RuntimeRoot, path) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static void AssertNoPlaintext(SecretTestRuntime fixture)
    {
        foreach (var path in Directory.EnumerateFiles(fixture.Instance.RuntimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "instance.lock" && !path.EndsWith("-shm", StringComparison.Ordinal)))
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Canary)) < 0);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Canary)) < 0);
        }
    }
}
