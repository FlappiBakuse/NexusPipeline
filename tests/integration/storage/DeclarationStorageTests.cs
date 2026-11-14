using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DeclarationStorageTests
{
    [Fact(DisplayName = "Actual Definition Rows Persist All Adapter Access And Reject Incomplete Stored Declarations")]
    public void PersistsAdapterDeclarations()
    {
        using var fixture = new PlanBuilderFixture(saveConfigurations: false, otherExtensionId: "bettergi");
        var before = fixture.Definitions.Read();
        var completion = new ConfigurationSelector([new ConfigurationProperty("CompletionAction")]);
        var script = before.Scripts.Single(item => item.Id == fixture.OtherScriptId) with
        {
            ConfigurationPaths = before.Scripts.Single(item => item.Id == fixture.ScriptId).ConfigurationPaths,
            AdapterResources = [new("one-dragon", 0, "", 1, ConfigurationSyntax.Json,
                new([new(completion, false)], [new(completion, false)], [new([new ConfigurationProperty("TaskDefinitions")])]))]
        };
        var change = new AutomationChanges(before.Revision, [], [script], [], [], [], [], []);
        fixture.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "adapter-declaration-test", change), change, DateTimeOffset.UtcNow);
        var actual = Assert.Single(fixture.Definitions.Read().Scripts.Single(item => item.Id == fixture.OtherScriptId).AdapterResources);
        Assert.Equal("one-dragon", actual.ResourceId); Assert.Equal(ConfigurationSyntax.Json, actual.Syntax);
        Assert.False(Assert.Single(actual.Access.Write).Descendants);
        Assert.Equal("TaskDefinitions", Assert.IsType<ConfigurationProperty>(Assert.Single(Assert.Single(actual.Access.Protected).Steps)).Name);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Notifications | DatabaseDomain.Content);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE automation.automations SET adapter_resources_json=json_remove(adapter_resources_json,'$[0].access.protected') WHERE automation_id=$id;";
        command.Parameters.AddWithValue("$id", fixture.OtherScriptId.ToString("D")); Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Throws<IOException>(() => fixture.Definitions.Read());
    }
}
