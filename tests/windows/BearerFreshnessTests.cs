using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class BearerFreshnessTests
{
    [Theory(DisplayName = "An Already Minted Bearer Rechecks Its Actual Producer Before Every Sensitive Use")]
    [InlineData("subject")]
    [InlineData("operation")]
    [InlineData("history")]
    public async Task RechecksProducerIdentity(string fault)
    {
        using var fixture = SecretTestRuntime.Create();
        const string token = "control_Actual_Minted_Producer_Proof_032";
        var operation = Guid.NewGuid();
        using var material = new SecretValue(token);
        await fixture.Service.ApplyAsync(operation, SecretFieldKey.HostControlBearer(fixture.Instance.InstanceId),
            0, SecretDisposition.Set, material, default);
        using var verifier = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
        var proof = Assert.IsType<VerifiedControlBearer>(verifier.Verify(token));
        proof.RequireCurrent();
        if (fault == "history")
        {
            using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT journal_id FROM secret_preparations WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operation.ToString("D"));
            var journal = (string)query.ExecuteScalar()!;
            var path = Path.Combine(fixture.Instance.RuntimeRoot, "journals", journal, "0000000001.json");
            var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            document["instanceId"] = Guid.NewGuid().ToString("D");
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document)));
            verifier.Dispose();
            Assert.Throws<ObjectDisposedException>(() => proof.RequireCurrent());
            File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document));
        }
        else
        {
            using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath);
            using var query = connection.CreateCommand();
            query.CommandText = fault == "subject"
                ? "UPDATE operations SET subject_id='local:another-owner' WHERE operation_id=$id;"
                : "UPDATE operations SET status='accepted' WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operation.ToString("D"));
            Assert.Equal(1, query.ExecuteNonQuery());
        }
        var before = Fingerprints(fixture.Instance.RuntimeRoot);
        if (fault == "history")
        {
            using var reopened = new ControlBearerVerifier(fixture.Instance, fixture.Database, fixture.Lease, fixture.Index, fixture.Service);
            Assert.Throws<IOException>(() => reopened.Verify(token));
        }
        else
        {
            Assert.Throws<IOException>(() => proof.RequireCurrent());
            Assert.Throws<IOException>(() => proof.RequireCurrent());
        }
        Assert.Equal(before, Fingerprints(fixture.Instance.RuntimeRoot));
    }

    private static string[] Fingerprints(string root) =>
        new[] { "journals", "secrets" }.SelectMany(name => Directory.EnumerateFiles(Path.Combine(root, name),
            "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
