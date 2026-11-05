using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class AdapterIntentTests
{
    private const string Reply = """
        globalThis.nexusTaskAdapter = input => {
          const patches = JSON.parse(JSON.stringify(input.configurationIntents));
          CHANGE
          return {format:input.format,version:input.version,direction:'response',phase:input.phase,
            extensionId:input.extensionId,operationId:input.operationId,configPatches:patches,diagnostics:[]};
        };
        """;

    [Fact(DisplayName = "The Actual Interpreter Returns Only The Explicit Resource Bound Configuration Intent")]
    public async Task ProposesBoundEdits()
    {
        await using var fixture = new TaskScriptFixture();
        Prepare(fixture);
        await fixture.ActivateAsync();
        var result = fixture.Execute(fixture.Executor(Reply.Replace("CHANGE", "", StringComparison.Ordinal)));
        var patch = Assert.Single(result.ConfigPatches!);
        Assert.Equal(fixture.Request.OperationId, patch.OperationId);
        Assert.Equal("enabled", Assert.Single(Assert.Single(patch.Edits).Selector).GetString());
        Assert.True(Assert.Single(patch.Edits).Value.GetBoolean());
    }

    [Theory(DisplayName = "A Plugin Cannot Widen User Intent Or Substitute Another Resource Revision Operation Or Value")]
    [InlineData("patches[0].edits[0].value = false;")]
    [InlineData("patches[0].edits[0].selector = ['other'];")]
    [InlineData("patches[0].expectedRevision = 2;")]
    [InlineData("patches[0].resourceId = 'another';")]
    [InlineData("patches[0].operationId = '00000000-0000-4000-8000-000000000001';")]
    public async Task RejectsExpandedIntent(string change)
    {
        await using var fixture = new TaskScriptFixture();
        Prepare(fixture);
        await fixture.ActivateAsync();
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(Reply.Replace("CHANGE", change, StringComparison.Ordinal))));
    }

    [Theory(DisplayName = "The Host Rejects An Unbound Intent Before The Interpreter Can Execute")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnboundIntent(bool wrongPhase)
    {
        await using var fixture = new TaskScriptFixture();
        Prepare(fixture);
        fixture.Request = wrongPhase ? fixture.Request with { Phase = "discover" } : fixture.Request with
        { ConfigurationIntents = [fixture.Request.ConfigurationIntents![0] with { SourceSha256 = new string('0', 64) }] };
        await fixture.ActivateAsync();
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor("throw new Error('must_not_execute');")));
    }

    private static void Prepare(TaskScriptFixture fixture)
    {
        const string content = "{\"enabled\":false,\"other\":7}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        fixture.Request = fixture.Request with
        {
            Phase = "configuration",
            Resources = [new() { ResourceId = "settings", Content = content, Sha256 = digest, Revision = 1 }],
            ConfigurationIntents = [new()
            {
                Format = "nexus.config.patch", Version = 1, OperationId = fixture.Request.OperationId,
                ResourceId = "settings", SourceSha256 = digest, ExpectedRevision = 1,
                Edits = [new() { Selector = [JsonSerializer.SerializeToElement("enabled")],
                    Expected = JsonSerializer.SerializeToElement(false), Value = JsonSerializer.SerializeToElement(true) }]
            }]
        };
    }

    [Theory(DisplayName = "The Actual Interpreter Preserves Explicit Existence And Cannot Substitute Missing For Null")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindsExpectedExistence(bool exists)
    {
        await using var fixture = new TaskScriptFixture(); Prepare(fixture);
        var patch = fixture.Request.ConfigurationIntents![0];
        fixture.Request = fixture.Request with { ConfigurationIntents = [patch with
            { Edits = [patch.Edits[0] with { Expected = JsonSerializer.SerializeToElement<object?>(null), ExpectedExists = exists }] }] };
        await fixture.ActivateAsync();
        var result = fixture.Execute(fixture.Executor(Reply.Replace("CHANGE", "", StringComparison.Ordinal)));
        Assert.Equal(exists, Assert.Single(Assert.Single(result.ConfigPatches!).Edits).ExpectedExists);
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(Reply.Replace("CHANGE",
            "patches[0].edits[0].expectedExists = !patches[0].edits[0].expectedExists;", StringComparison.Ordinal))));
    }
}
