using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class TaskPatchTests
{
    [Fact(DisplayName = "Configuration Proposal Retains Host Operation Digest And Revision")]
    public async Task BindsProposalRevision()
    {
        await using var fixture = await FixtureAsync();
        var patch = Patch(fixture);
        var result = fixture.Execute(fixture.Executor(Script(new JsonArray(patch))));
        var accepted = Assert.Single(result.ConfigPatches!);
        Assert.Equal(fixture.Request.OperationId, accepted.OperationId);
        Assert.Equal(fixture.Request.Resources![0].Sha256, accepted.SourceSha256);
        Assert.Equal(7, accepted.ExpectedRevision);
    }

    [Theory(DisplayName = "Configuration Proposal Cannot Escape Its Frozen Resource")]
    [InlineData("resource")]
    [InlineData("sha")]
    [InlineData("revision")]
    [InlineData("operation")]
    [InlineData("phase")]
    [InlineData("duplicate")]
    public async Task RejectsUnboundResources(string changed)
    {
        await using var fixture = await FixtureAsync();
        var patch = Patch(fixture);
        switch (changed)
        {
            case "resource": patch["resourceId"] = "foreign"; break;
            case "sha": patch["sourceSha256"] = new string('0', 64); break;
            case "revision": patch["expectedRevision"] = 8; break;
            case "operation": patch["operationId"] = Guid.NewGuid().ToString("D"); break;
            case "phase": fixture.Request = fixture.Request with { Phase = "discover" }; break;
        }
        var patches = new JsonArray(patch);
        if (changed == "duplicate") patches.Add(patch.DeepClone());
        Assert.Throws<JsonException>(() => fixture.Execute(fixture.Executor(Script(patches))));
    }

    private static async Task<TaskScriptFixture> FixtureAsync()
    {
        var fixture = new TaskScriptFixture();
        fixture.Request = fixture.Request with { Phase = "configuration", Resources = [new()
        {
            ResourceId = "config", Content = "{}", Sha256 = Convert.ToHexStringLower(SHA256.HashData("{}"u8)), Revision = 7
        }] };
        await fixture.ActivateAsync();
        return fixture;
    }

    private static JsonNode Patch(TaskScriptFixture fixture) => JsonSerializer.SerializeToNode(new
    {
        format = "nexus.config.patch", version = 1, operationId = fixture.Request.OperationId, resourceId = "config",
        sourceSha256 = fixture.Request.Resources![0].Sha256, expectedRevision = 7,
        edits = new[] { new { selector = new[] { "CompletionAction" }, expected = "Shutdown", value = "None" } }
    })!;

    private static string Script(JsonArray patches) =>
        "globalThis.nexusTaskAdapter = input => ({...input,direction:'response',resources:undefined,tasks:[],diagnostics:[],configPatches:" + patches.ToJsonString() + "});";
}
