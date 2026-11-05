using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DocumentPortTests
{
    [Theory(DisplayName = "The Actual Installed Sdk Secret Document Port Publishes Under Manifest And Current Field Authority And Disposes Drafts On Every Outcome")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsesDeclaredAuthority(bool declared)
    {
        using var catalog = new ExtensionCatalogFixture();
        var policy = new Policy();
        var secrets = new SecretFieldService(catalog.Instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, new());
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(null, secrets, policy, null, null, null),
            managedManifestEdit: manifest => manifest["permissions"]!["secretAccess"] = declared ? new JsonArray("own-extension") : new JsonArray());
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        var subjectId = Guid.NewGuid(); var operationId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { revision = 0, secretRevision = 0, subjectId, kind = "set", document = new { formatVersion = 1 } });
        try
        {
            if (!declared)
            {
                await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("secret-save", operationId, "actual:editor", false, payload, () => { }, default).AsTask());
                Assert.Equal(0, (await secrets.GetStateAsync(new("fixture-plugin", subjectId, "credential"), default)).Revision);
                Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "secret-draft-retired")));
                return;
            }
            var result = await frontend.InvokeAsync("secret-save", operationId, "actual:editor", false, payload, () => { }, default);
            using var commit = JsonDocument.Parse(result.Content);
            Assert.Equal(1, commit.RootElement.GetProperty("Revision").GetInt64());
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "secret-draft-retired")));
            using (var value = await secrets.ReadAsync(new("fixture-plugin", subjectId, "credential"), default))
                value.Use(text => Assert.True(text.SequenceEqual("synthetic-installed-value")));
            var lookup = JsonSerializer.SerializeToUtf8Bytes(new { operationId });
            var receipt = await frontend.InvokeAsync("secret-commit", null, "actual:editor", false, lookup, () => { }, default);
            using var saved = JsonDocument.Parse(receipt.Content); Assert.Equal(1, saved.RootElement.GetProperty("Revision").GetInt64());
            receipt = await frontend.InvokeAsync("secret-commit", null, "other:editor", false, lookup, () => { }, default);
            using var foreign = JsonDocument.Parse(receipt.Content); Assert.Equal(JsonValueKind.Null, foreign.RootElement.ValueKind);
            File.Delete(Path.Combine(fixture.ActivationDirectory, "secret-draft-retired"));
            policy.Allowed = false;
            var next = JsonSerializer.SerializeToUtf8Bytes(new { revision = 1, secretRevision = 1, subjectId, kind = "set", document = new { formatVersion = 1 } });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("secret-save", Guid.NewGuid(), "actual:editor", false, next, () => { }, default).AsTask());
            Assert.Equal(1, (await secrets.GetStateAsync(new("fixture-plugin", subjectId, "credential"), default)).Revision);
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "secret-draft-retired")));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class Policy : ISecretAccessPolicy
    {
        internal bool Allowed { get; set; } = true;
        public void AssertAllowed(SecretFieldKey key, SecretAccessKind access, SecretSubjectScopes declaredScopes)
        { if (!Allowed || !declaredScopes.HasFlag(SecretSubjectScopes.OwnExtension)) throw new UnauthorizedAccessException(); }
    }
}
