using ExecutionTarget = NexusPipeline.Core.Execution.ExecutionTarget;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StartSourceTests
{
    [Theory(DisplayName = "The Original Host Retry Approval And Backoff Round Trip Through The Existing Revisioned Settings Owner")]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistsRetryApproval(bool approved)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var before = source.Settings.Read();
        Assert.False(before.Values.AutomaticRetry.Enabled);
        var change = new HostSettingsChange(before.Revision, before.Values with { AutomaticRetry = new(approved, 7) });
        var result = source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retry-settings", change), change, DateTimeOffset.UtcNow);
        Assert.Equal(new AutomaticRetryPreferences(approved, 7), result.Saved.Values.AutomaticRetry);
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal(HostSettings.Default.AutomaticRetry, HostSettingsRows.Read(connection, transaction, before.Revision).Values.AutomaticRetry);
    }

    [Theory(DisplayName = "The Actual Accept Transaction Binds Every Original Target To Its Immutable Attempt Request")]
    [InlineData("binding")]
    [InlineData("script")]
    [InlineData("script-user")]
    [InlineData("queue")]
    public void BindsTargetSources(string kind)
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        ExecutionTarget target = kind switch
        {
            "binding" => new BindingTarget(source.OtherBinding),
            "script" => new ScriptTarget(source.OtherScriptId),
            "script-user" => new ScriptUserTarget(source.OtherScriptId, source.UserB),
            _ => new QueueTarget(source.QueueId)
        };
        var request = source.Request(target); var operation = Guid.NewGuid(); const string subject = "start-source-fixture";
        var accepted = fixture.Service.Start(operation, subject, request, DateTimeOffset.UtcNow);
        using var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        var frozen = StartSourceRows.Read(connection, transaction, operation);
        Assert.Equal(request, frozen.Request); Assert.Equal(subject, frozen.Identity.SubjectId);
        Assert.Equal(accepted.JournalId, frozen.JournalId);
        var attempt = new AttemptPlanCodec(source.Instance, new(), new(source.Instance, new())).Read(accepted.Plan).ReadWire();
        StartSourceRows.RequireAttempt(frozen, attempt);
        Assert.NotNull(attempt.StartRequest); Assert.Equal(attempt.RunId, frozen.RunId.ToString("D"));
        var count = fixture.Count("operations");
        Assert.False(fixture.Service.Start(operation, subject, request, DateTimeOffset.UtcNow).Acceptance.IsNew);
        Assert.Equal(count, fixture.Count("operations"));
        StartSourceRows.VerifyInventory(source.Instance, source.Database, new());
    }

    [Fact(DisplayName = "A Real Sql Source Failure Rolls Back The Original Start And Cannot Leave An Accepted Operation Without Its Source")]
    public void RevertsSourceFailure()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var operation = Guid.NewGuid(); var before = fixture.Counts();
        fixture.Execute("CREATE TRIGGER main.fail_start_source BEFORE INSERT ON execution_start_sources BEGIN SELECT RAISE(ABORT,'source-fixture'); END;");
        try
        {
            Assert.Throws<SqliteException>(() => fixture.Service.Start(operation, "source-rollback",
                source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow));
            Assert.Equal(before, fixture.Counts()); Assert.Equal(0, fixture.Count("execution_start_sources"));
            Assert.Equal(0, fixture.Count("journal_writer_job_roots"));
        }
        finally { fixture.Execute("DROP TRIGGER main.fail_start_source;"); }
        var actual = fixture.Service.Start(operation, "source-rollback", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        Assert.True(actual.Acceptance.IsNew); Assert.Equal(1, fixture.Count("execution_start_sources"));
    }

    [Theory(DisplayName = "Read Only Startup Rejects Orphaned Or Changed Source Metadata Without Repairing The Original Database")]
    [InlineData("source")]
    [InlineData("subject")]
    [InlineData("digest")]
    public void PreservesChangedSources(string field)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        _ = fixture.Service.Start(Guid.NewGuid(), "source-diagnostic", source.Request(new BindingTarget(source.OtherBinding)), DateTimeOffset.UtcNow);
        fixture.Execute(field switch
        {
            "source" => "DELETE FROM main.execution_start_sources;",
            "subject" => "UPDATE main.execution_start_sources SET subject_id='another-subject';",
            _ => "UPDATE main.operations SET request_digest='" + new string('a', 64) + "' WHERE operation_kind='execution.start';"
        });
        var before = File.ReadAllBytes(source.Database.DatabasePath);
        var wal = source.Database.DatabasePath + "-wal"; var walBefore = File.Exists(wal) ? File.ReadAllBytes(wal) : null;
        Assert.ThrowsAny<Exception>(() => StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "source-diagnostic-copy"),
            new JournalStartupAdmission(source.Instance, new())));
        Assert.Equal(before, File.ReadAllBytes(source.Database.DatabasePath));
        Assert.Equal(walBefore, File.Exists(wal) ? File.ReadAllBytes(wal) : null);
    }

    [Theory(DisplayName = "The Existing Identity Owner Rejects Unknown Source Request Syntax")]
    [InlineData("kind")]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("extra")]
    public void RejectsUnknownSyntax(string damage)
    {
        var request = new ExecutionSelectionRequest(new BindingTarget(Guid.NewGuid()), 7, 9);
        var bytes = ExecutionStartIdentity.Encode(request);
        var node = System.Text.Json.Nodes.JsonNode.Parse(bytes)!.AsObject();
        switch (damage)
        {
            case "kind": node["targetKind"] = "user"; break;
            case "id": node["ids"]![0] = Guid.Empty.ToString("D"); break;
            case "revision": node["expectedCatalogRevision"] = -1; break;
            default: node["unknown"] = true; break;
        }
        Assert.ThrowsAny<Exception>(() => ExecutionStartIdentity.Decode(JsonSerializer.SerializeToUtf8Bytes(node)));
    }
}
