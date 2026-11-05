using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class PatchBatchTests
{
    [Fact(DisplayName = "Multiple Documents In One Resource And Additional Resources Publish Once Without Changing Live Native Objects")]
    public void PublishesPatchOnce()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        var originals = request.Select(item => fixture.SavedText(item.Address, item.RelativePath)).ToArray();
        var fingerprints = fixture.Fingerprints();
        using var preview = fixture.Service.Preview("fixture", request);
        Assert.Equal(3, preview.DocumentCount); Assert.Equal(2, preview.ResourceCount); Assert.True(preview.HasChanges);
        Assert.Equal(fingerprints, fixture.Fingerprints());
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(preview));
        var result = fixture.Service.Commit(preview, DateTimeOffset.UtcNow);
        Assert.True(result.IsNew); Assert.Equal(new[] { "extra", "main" }, result.Items.Select(item => item.Saved.Address.ResourceId));
        for (var index = 0; index < request.Length; index++)
            Assert.Equal(originals[index].Replace("true", "false", StringComparison.Ordinal), fixture.SavedText(request[index].Address, request[index].RelativePath));
        fixture.AssertSavedBaselines(2);
        Assert.Equal(1, fixture.Scalar("SELECT count(*) FROM configuration_batch_results WHERE source_request_digest IS NOT NULL;"));
        Assert.Equal(2, fixture.Scalar("SELECT resource_count FROM configuration_batch_results WHERE source_request_digest IS NOT NULL;"));
        _ = StateDatabase.OpenExisting(fixture.Base.Instance, Path.Combine(fixture.Base.Root, "diagnostics"), new JournalStartupAdmission(fixture.Base.Instance, new()));
    }

    [Theory(DisplayName = "Any Invalid Document Rejects The Whole Patch Batch Before Persistent Preparation")]
    [InlineData("expected")]
    [InlineData("protected")]
    [InlineData("readonly")]
    [InlineData("opaque")]
    [InlineData("syntax")]
    [InlineData("sha")]
    public void RejectsInvalidDocuments(string reason)
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        request[2] = reason switch
        {
            "expected" => fixture.EditWire(request[2], "edits", new[] { new { selector = new[] { "flag" }, expected = false, value = false } }),
            "protected" => fixture.EditWire(request[2], "edits", new[] { new { selector = new[] { "secret" }, expected = "fixture-private", value = "changed" } }),
            "readonly" => request[2] with { Access = request[2].Access with { Write = [] } },
            "opaque" => request[2] with { Syntax = ConfigurationSyntax.Opaque },
            "syntax" => request[2] with { Syntax = (ConfigurationSyntax)99 },
            _ => fixture.EditWire(request[2], "sourceSha256", new string('b', 64))
        };
        var before = fixture.Fingerprints();
        Assert.Throws<ConfigurationException>(() => fixture.Service.Preview("fixture", request));
        Assert.Equal(before, fixture.Fingerprints()); fixture.AssertSavedBaselines(1);
    }

    [Theory(DisplayName = "Duplicate Or Mixed Patch Owners And Inconsistent Resource Baselines Cannot Become A Prepared Batch")]
    [InlineData("duplicate")]
    [InlineData("case-alias")]
    [InlineData("operation")]
    [InlineData("binding")]
    [InlineData("binding-revision")]
    [InlineData("source-revision")]
    [InlineData("contract")]
    public void RejectsMixedOwners(string reason)
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        request[1] = reason switch
        {
            "duplicate" => request[0],
            "case-alias" => request[0] with { RelativePath = "ONE.JSONC" },
            "operation" => fixture.EditWire(request[1], "operationId", Guid.NewGuid()),
            "binding" => request[1] with { Address = new(fixture.Base.OtherBinding, "main") },
            "binding-revision" => request[1] with { ExpectedBindingRevision = 2 },
            "source-revision" => fixture.EditWire(request[1], "expectedRevision", 2),
            _ => request[1] with { Contract = request[1].Contract with { Revision = 2 } }
        };
        var before = fixture.Fingerprints();
        Assert.Throws<ConfigurationException>(() => fixture.Service.Preview("fixture", request));
        Assert.Equal(before, fixture.Fingerprints()); fixture.AssertSavedBaselines(1);
    }

    [Fact(DisplayName = "A Prepared Patch Batch Owns Frozen Candidates And Source Leases Despite Caller Input Mutation")]
    public void OwnsFrozenCandidates()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        var source = fixture.Base.Revisions.Read(fixture.Main)!.Tree.Entries.Single(item => item.RelativePath == "one.jsonc");
        var preview = fixture.Service.Preview("fixture", request);
        try
        {
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(fixture.Content, source.Sha256 + ".bin")));
            var memory = System.Runtime.InteropServices.MemoryMarshal.AsMemory(request[0].PatchBytes);
            memory.Span.Fill(0);
            request[1] = request[1] with { Access = request[1].Access with { Write = [] }, RelativePath = "other.yaml" };
            fixture.Service.Commit(preview, DateTimeOffset.UtcNow);
            Assert.Contains("\"flag\":false", fixture.SavedText(fixture.Main, "one.jsonc"));
            Assert.Contains("flag: false", fixture.SavedText(fixture.Main, "two.yaml"));
            fixture.AssertSavedBaselines(2);
        }
        finally { preview.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => fixture.Service.Commit(preview, DateTimeOffset.UtcNow));
        File.Delete(Path.Combine(fixture.Content, source.Sha256 + ".bin"));
    }

    [Theory(DisplayName = "Completed Batch Replay Binds The Full Request Before Opening Any Retired Source")]
    [InlineData("subject")]
    [InlineData("order")]
    [InlineData("bytes")]
    [InlineData("access")]
    [InlineData("contract")]
    [InlineData("path")]
    public void BindsReplayRequest(string difference)
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        var source = fixture.Base.Revisions.Read(fixture.Main)!.Tree.Entries.Single(item => item.RelativePath == "one.jsonc");
        fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow);
        File.Delete(Path.Combine(fixture.Content, source.Sha256 + ".bin"));
        var altered = request.ToArray();
        if (difference == "order") Array.Reverse(altered);
        if (difference == "bytes") altered[0] = altered[0] with { PatchBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(altered[0].PatchBytes.Span) + "\n") };
        if (difference == "access") altered[0] = altered[0] with { Access = altered[0].Access with { Read = [new(new([]), true)] } };
        if (difference == "contract") altered[2] = altered[2] with { Contract = altered[2].Contract with { Revision = 2 } };
        if (difference == "path") altered[2] = altered[2] with { RelativePath = "another.json" };
        var service = new PatchBatchService(fixture.Base.Instance, fixture.Base.Database, new(), fixture.Lease, fixture.Resources);
        Assert.Throws<OperationConflictException>(() => service.Apply(difference == "subject" ? "other-subject" : "fixture", altered, DateTimeOffset.UtcNow));
        fixture.AssertSavedBaselines(2);
    }

    [Fact(DisplayName = "Exact Batch Replay Returns Original Results During Closure Without Rewinding Newer Snapshots Or Reading Old Content")]
    public void KeepsHistoricalResults()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        var old = fixture.Base.Revisions.Read(fixture.Main)!.Tree;
        fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow);
        fixture.Base.Save(new([fixture.Base.Capture("main", "newer-main", 2), fixture.Base.Capture("extra", "newer-extra", 2)]));
        foreach (var entry in old.Entries.Where(entry => entry.Kind == FileObjectKind.File && entry.RelativePath != "opaque.bin"))
            File.Delete(Path.Combine(fixture.Content, entry.Sha256 + ".bin"));
        fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        var replay = new PatchBatchService(fixture.Base.Instance, fixture.Base.Database, new(), fixture.Lease, fixture.Resources)
            .Apply("fixture", request, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew); Assert.All(replay.Items, item => Assert.Equal(2, item.Saved.Revision));
        fixture.AssertSavedBaselines(3);
    }

    [Theory(DisplayName = "Historical Patch Batch Results Survive Actual Definition Deletion And Replay Without The Retired Source")]
    [InlineData("binding")]
    [InlineData("user")]
    [InlineData("script")]
    public void ReplaysDeletedDefinitions(string removed)
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid());
        var old = fixture.Base.Revisions.Read(fixture.Main)!.Tree.Entries.Single(item => item.RelativePath == "one.jsonc");
        var result = fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow);
        var definitions = new AutomationDefinitionStore(fixture.Base.Database); var catalog = definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == fixture.Base.Binding);
        var user = catalog.Users.Single(item => item.Id == binding.UserId);
        var script = catalog.Scripts.Single(item => item.Id == binding.ScriptId);
        var deletion = removed switch
        {
            "binding" => new RemoveAutomationDefinition(AutomationDefinitionKind.Binding, binding.Id, binding.Revision),
            "user" => new RemoveAutomationDefinition(AutomationDefinitionKind.User, user.Id, user.Revision),
            _ => new RemoveAutomationDefinition(AutomationDefinitionKind.Script, script.Id, script.Revision)
        };
        var changes = new AutomationChanges(catalog.Revision, [], [], [], [], [], [], [deletion]);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(definitions.Read().Bindings, item => item.Id == fixture.Base.Binding);
        Assert.Equal(0, fixture.Scalar($"SELECT count(*) FROM configuration_current WHERE binding_id='{fixture.Base.Binding:D}';"));
        File.Delete(Path.Combine(fixture.Content, old.Sha256 + ".bin"));
        var before = fixture.Fingerprints();
        var database = StateDatabase.OpenExisting(fixture.Base.Instance, Path.Combine(fixture.Base.Root, "diagnostics"),
            new JournalStartupAdmission(fixture.Base.Instance, new()));
        var replay = new PatchBatchService(fixture.Base.Instance, database, new(), fixture.Lease, fixture.Resources)
            .Apply("fixture", request, DateTimeOffset.UtcNow);
        Assert.False(replay.IsNew);
        for (var index = 0; index < result.Items.Length; index++)
        {
            Assert.Equal(result.Items[index].Saved.Revision, replay.Items[index].Saved.Revision);
            Assert.Equal(result.Items[index].Saved.UserId, replay.Items[index].Saved.UserId);
            Assert.Equal(result.Items[index].Saved.ScriptId, replay.Items[index].Saved.ScriptId);
            Assert.Equal(result.Items[index].Saved.Contract, replay.Items[index].Saved.Contract);
            Assert.Equal(result.Items[index].Saved.Tree.Entries, replay.Items[index].Saved.Tree.Entries);
        }
        Assert.Equal(before, fixture.Fingerprints()); fixture.VerifyOriginal();
    }

    [Theory(DisplayName = "Patch Batch Sql Failure Rolls Back Every Resource Result Header And Acceptance And Can Retry The Same Preparation")]
    [InlineData("configuration_batch_results", "NEW.source_request_digest IS NOT NULL")]
    [InlineData("configuration_batch_items", "NEW.ordinal=1")]
    [InlineData("configuration_revision_assets", "NEW.resource_id='main' AND NEW.revision=2")]
    public void RevertsBatchFailure(string table, string when)
    {
        using var fixture = new PatchBatchFixture();
        using var prepared = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid()));
        var operations = fixture.Base.Count("operations"); var blobs = fixture.Base.Count("blobs");
        fixture.Base.Execute($"CREATE TRIGGER fixture_patch_fail BEFORE INSERT ON {table} WHEN {when} BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        Assert.Throws<SqliteException>(() => fixture.Service.Commit(prepared, DateTimeOffset.UtcNow));
        fixture.AssertSavedBaselines(1);
        Assert.Equal(operations, fixture.Base.Count("operations")); Assert.Equal(blobs, fixture.Base.Count("blobs"));
        Assert.Equal(1, fixture.Base.Count("configuration_batch_results")); Assert.Equal(2, fixture.Base.Count("configuration_batch_items"));
        Assert.Equal(2, fixture.Base.Count("configuration_revisions"));
        fixture.Base.Execute("DROP TRIGGER fixture_patch_fail;");
        Assert.True(fixture.Service.Commit(prepared, DateTimeOffset.UtcNow).IsNew); fixture.AssertSavedBaselines(2);
        Assert.False(fixture.Service.Commit(prepared, DateTimeOffset.UtcNow).IsNew);
    }

    [Theory(DisplayName = "Damaged Patch Batch Result Metadata Is Rejected On An Isolated Startup Diagnostic Copy")]
    [InlineData("tail")]
    [InlineData("header")]
    [InlineData("all-items")]
    [InlineData("null-digest")]
    [InlineData("invalid-digest")]
    [InlineData("kind")]
    public void PreservesDamagedResults(string damage)
    {
        using var fixture = new PatchBatchFixture();
        var operation = Guid.NewGuid(); fixture.Service.Apply("fixture", fixture.Request(operation), DateTimeOffset.UtcNow);
        var id = operation.ToString("D");
        fixture.Base.Execute(damage switch
        {
            "tail" => $"DELETE FROM configuration_batch_items WHERE operation_id='{id}' AND ordinal=1;",
            "header" => $"DELETE FROM configuration_batch_results WHERE operation_id='{id}';",
            "all-items" => $"DELETE FROM configuration_batch_items WHERE operation_id='{id}';",
            "null-digest" => $"UPDATE configuration_batch_results SET source_request_digest=NULL WHERE operation_id='{id}';",
            "invalid-digest" => $"UPDATE configuration_batch_results SET source_request_digest='{new string('z', 64)}' WHERE operation_id='{id}';",
            _ => $"UPDATE operations SET operation_kind='configuration.run.sync' WHERE operation_id='{id}';"
        });
        var before = fixture.Fingerprints();
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Base.Instance, Path.Combine(fixture.Base.Root, "diagnostics"),
            new JournalStartupAdmission(fixture.Base.Instance, new())));
        Assert.Equal(before, fixture.Fingerprints()); fixture.AssertSavedBaselines(2);
    }

    [Fact(DisplayName = "A Valid Hex Header Forged For Another Complete Request Cannot Bypass The Accepted Operation Digest")]
    public void RejectsForgedHeaders()
    {
        using var fixture = new PatchBatchFixture();
        var operation = Guid.NewGuid(); var request = fixture.Request(operation); var altered = request.ToArray();
        altered[0] = altered[0] with { Access = altered[0].Access with { Read = [new(new([]), true)] } };
        using var different = fixture.Service.Preview("fixture", altered);
        var replacementDigest = different.Use((batch, _, _) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(
            batch.Changes.Select(change => new { change.Address, Digest = change.SourceRequestDigest }).ToArray()))));
        fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow);
        fixture.Base.Execute($"UPDATE configuration_batch_results SET source_request_digest='{replacementDigest}' WHERE operation_id='{operation:D}';");
        var before = fixture.Fingerprints();
        Assert.Throws<OperationConflictException>(() => fixture.Service.Apply("fixture", altered, DateTimeOffset.UtcNow));
        Assert.Equal(before, fixture.Fingerprints()); fixture.AssertSavedBaselines(2);
    }

    [Theory(DisplayName = "A Whole Patch Batch Needs Every Shared Claim Before Any Candidate Can Be Persisted")]
    [InlineData("resource")]
    [InlineData("closing")]
    [InlineData("recovery")]
    public void RequiresWholeAdmission(string reason)
    {
        using var fixture = new PatchBatchFixture();
        using var preview = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid()));
        using var conflict = reason == "resource" ? fixture.Resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationRevisionService.ResourceKey(fixture.Extra), ResourceClaimMode.Mutation)]).Lease : null;
        if (reason == "closing") fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        if (reason == "recovery") fixture.Index.BlockUnknown("fixture_unknown");
        var before = fixture.Fingerprints();
        Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(preview, DateTimeOffset.UtcNow));
        Assert.Equal(before, fixture.Fingerprints()); fixture.AssertSavedBaselines(1);
    }

    [Theory(DisplayName = "A Stale Resource Or Binding After Patch Preflight Cannot Partially Publish Or Consume The Operation")]
    [InlineData("snapshot")]
    [InlineData("binding")]
    public void RejectsStalePreflight(string changed)
    {
        using var fixture = new PatchBatchFixture();
        using var preview = fixture.Service.Preview("fixture", fixture.Request(Guid.NewGuid()));
        if (changed == "snapshot") fixture.Base.Save(new([fixture.Base.Capture("extra", "newer", 1)]));
        else
        {
            var store = new AutomationDefinitionStore(fixture.Base.Database); var catalog = store.Read();
            var binding = catalog.Bindings.Single(item => item.Id == fixture.Base.Binding);
            var changes = new AutomationChanges(catalog.Revision, [], [], [], [binding with { IsEnabled = true }], [], [], []);
            store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        }
        var operations = fixture.Base.Count("operations"); var headers = fixture.Base.Count("configuration_batch_results");
        Assert.Throws<ConfigurationException>(() => fixture.Service.Commit(preview, DateTimeOffset.UtcNow));
        Assert.Equal(operations, fixture.Base.Count("operations")); Assert.Equal(headers, fixture.Base.Count("configuration_batch_results"));
        Assert.Equal(0, fixture.Scalar($"SELECT count(*) FROM operations WHERE operation_id='{preview.OperationId:D}';"));
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Main)!.Revision);
        Assert.Equal(changed == "snapshot" ? 2 : 1, fixture.Base.Revisions.Read(fixture.Extra)!.Revision);
        fixture.VerifyOriginal();
    }

    [Fact(DisplayName = "No Change Patch Documents Keep Their Original Spelling And Generic Raw Save Does Not Invoke Repair")]
    public void KeepsUnchangedSpelling()
    {
        using var fixture = new PatchBatchFixture();
        var request = fixture.Request(Guid.NewGuid(), noChange: true);
        var before = request.Select(item => fixture.SavedText(item.Address, item.RelativePath)).ToArray();
        var result = fixture.Service.Apply("fixture", request, DateTimeOffset.UtcNow);
        Assert.All(result.Items, item => { Assert.False(item.HasChanges); Assert.Equal(1, item.Saved.Revision); });
        for (var index = 0; index < request.Length; index++) Assert.Equal(before[index], fixture.SavedText(request[index].Address, request[index].RelativePath));
        var raw = fixture.Base.Capture("main", "{\"flag\":true,\"arbitrary\":\"raw\"}", 1);
        var service = new ConfigurationRevisionService(fixture.Base.Instance, fixture.Base.Database, new(), fixture.Lease, fixture.Resources);
        service.Save(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "fixture", raw), raw, DateTimeOffset.UtcNow);
        Assert.Equal("{\"flag\":true,\"arbitrary\":\"raw\"}", fixture.SavedText(fixture.Main, ""));
        Assert.Equal(1, fixture.Base.Revisions.Read(fixture.Extra)!.Revision); fixture.VerifyOriginal();
    }
}
