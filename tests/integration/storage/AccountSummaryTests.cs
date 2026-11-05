using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Control;
using NexusPipeline.Host.Control.Automation;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AccountSummaryTests
{
    [Fact(DisplayName = "Account Aggregation Keeps The Worst Enabled Specialized Binding Even When Another Binding Has A Newer Success")]
    public void RanksBindingHistory()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var bindings = Specialized(fixture);
        var older = RecordStoreFixture.Record(101, source.QueueId);
        var newer = RecordStoreFixture.Record(201, source.QueueId);
        new RunRecordStore(source.Database).AppendCompleted([older, newer]);
        AddBinding(source.Database, older.FinalAttempt.RunId, bindings[0].Id, source.UserB, bindings[0].ScriptId, "bad", "latest");
        AddBinding(source.Database, newer.FinalAttempt.RunId, bindings[1].Id, source.UserB, bindings[1].ScriptId, "ok", "latest");
        var result = Read(fixture).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("userId").GetGuid() == source.UserB);
        Assert.Equal("bad", result.GetProperty("summaryTone").GetString());
        Assert.Equal(older.FinalAttempt.RunId, result.GetProperty("runId").GetGuid());
        Assert.Equal(2, result.GetProperty("bindings").GetArrayLength());
        Assert.Equal(0, result.GetProperty("activeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextRunAt").ValueKind);
    }

    [Fact(DisplayName = "Account Summary Distinguishes No Report From Disabled Effective Bindings And Preserves All Accounts")]
    public void DistinguishesAbsentReports()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source; Specialized(fixture);
        var first = Read(fixture).GetProperty("items");
        Assert.Equal(3, first.GetArrayLength());
        Assert.All(first.EnumerateArray(), item => Assert.Equal("not_run", item.GetProperty("summaryReason").GetString()));
        var catalog = source.Definitions.Read();
        var changes = new AutomationChanges(catalog.Revision, [], [], [],
            [.. catalog.Bindings.Select(binding => binding with { IsEnabled = false })], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "summary-fixture", changes), changes, DateTimeOffset.UtcNow);
        Assert.All(Read(fixture).GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("no_bindings", item.GetProperty("summaryReason").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("runId").ValueKind);
        });
    }

    [Fact(DisplayName = "Summary Access Is Rechecked Before Returning And A Revoked Mcp Reader Does Not Receive An Empty Success")]
    public void RechecksSummaryAuthority()
    {
        using var fixture = new ExecutionAdmissionFixture(); int checks = 0;
        var caller = new AuthenticatedControlRequest(ControlOrigin.Mcp, "summary-reader", null,
            () => new(++checks <= 4, false));
        Assert.Throws<UnauthorizedAccessException>(() => Service(fixture).Read(caller));
        Assert.Equal(5, checks);
    }

    [Fact(DisplayName = "Generic Native Work Does Not Add An Account Task Report Activity Badge")]
    public async Task IgnoresGenericActivity()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "child-wait");
        var running = fixture.Consumer.RunNextAsync();
        try
        {
            await fixture.Ready(".child.output-ready");
            Assert.Contains(Assert.Single(fixture.Consumer.ReadActive(ExecutionSequentialFixture.Subject)).Steps,
                step => step.Phase == "running");
            Assert.All(Read(fixture.Base, fixture.Consumer).GetProperty("items").EnumerateArray(),
                item => Assert.Equal(0, item.GetProperty("activeCount").GetInt32()));
        }
        finally { fixture.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Fact(DisplayName = "One Actual Specialized Provider Contributes One Active Account Task Report")]
    public async Task CountsProviderActivity()
    {
        await using var fixture = await ProviderRuntimeFixture.CreateAsync("wait");
        var running = fixture.RunAsync();
        try
        {
            await fixture.ReadyAsync();
            var items = Read(fixture.Base, fixture.Consumer).GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(1, items.Single(item => item.GetProperty("userId").GetGuid() == fixture.Source.UserB)
                .GetProperty("activeCount").GetInt32());
            Assert.Equal(1, items.Sum(item => item.GetProperty("activeCount").GetInt32()));
        }
        finally { fixture.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    private static AutomationBinding[] Specialized(ExecutionAdmissionFixture fixture)
    {
        var source = fixture.Source; var catalog = source.Definitions.Read();
        var scripts = catalog.Scripts.ToDictionary(script => script.Id, script => script with
            { Id = Guid.NewGuid(), Revision = 0, Kind = AutomationKind.Specialized, ExtensionId = "fixture-extension" });
        var bindings = catalog.Bindings.Select(binding => binding with
            { Id = Guid.NewGuid(), Revision = 0, ScriptId = scripts[binding.ScriptId].Id }).ToArray();
        var changes = new AutomationChanges(catalog.Revision, [], [.. scripts.Values], [], [.. bindings], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "summary-fixture", changes), changes, DateTimeOffset.UtcNow);
        return bindings.Where(binding => binding.UserId == source.UserB).ToArray();
    }
    private static AccountSummaries Service(ExecutionAdmissionFixture fixture, ExecutionRuntimeConsumer? active = null)
    {
        var source = fixture.Source;
        var definitions = new AutomationDefinitionService(source.Instance, new(), source.Database, source.Lease, fixture.Resources, fixture.Index);
        var execution = active ?? new ExecutionRuntimeConsumer(source.Instance, source.Database, new(), source.Lease, fixture.Resources, fixture.Index, fixture.Inbox);
        return new(definitions, new(source.Database), execution, TimeProvider.System);
    }
    private static JsonElement Read(ExecutionAdmissionFixture fixture, ExecutionRuntimeConsumer? active = null) => JsonSerializer.SerializeToElement(
        Service(fixture, active).Read(new(ControlOrigin.Local, "summary-reader", null, () => new(true, true))), JsonSerializerOptions.Web);
    private static void AddBinding(StateDatabase database, Guid run, Guid binding, Guid user, Guid script, string tone, string reason)
    {
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO binding_task_records VALUES($run,$binding,$user,$script,$tone,$reason)";
        foreach (var (key, value) in new[] { ("$run", run.ToString("D")), ("$binding", binding.ToString("D")),
            ("$user", user.ToString("D")), ("$script", script.ToString("D")), ("$tone", tone), ("$reason", reason) })
            command.Parameters.AddWithValue(key, value);
        command.ExecuteNonQuery();
    }
}
