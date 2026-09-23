using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution.Judgement;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class TaskProtocolScriptRunnerTests
{
    [Fact]
    public async Task MissingOptionalResourceIsCatchableWithoutExposingLocalPaths()
    {
        var result = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(
            "let message='';try{nexus.readResource('optional')}catch(error){message=error.message}console.log({message});",
            new {}, _ => throw new InvalidDataException(), _ => throw new InvalidDataException("private/local/path"), true, default);
        Assert.Equal("config_unavailable", result["message"]!.GetValue<string>());
    }
    [Fact]
    public async Task ActualJintReadsOnlyDeclaredBridgeAndReturnsJson()
    {
        var result = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(
            "console.log({value:nexus.readConfig('main').document.enabled, phase:nexus.input.phase, writable:typeof nexus.writeFile, probe:typeof nexus.httpGet});",
            new { phase = "discover" }, id => id == "main" ? "{\"document\":{\"enabled\":true}}" : throw new InvalidDataException(),
            _ => throw new InvalidDataException(), true, default);
        Assert.True(result["value"]!.GetValue<bool>());
        Assert.Equal("discover", result["phase"]!.GetValue<string>());
        Assert.Equal("undefined", result["writable"]!.GetValue<string>());
        Assert.Equal("undefined", result["probe"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("console.log({}); console.log({});")]
    [InlineData("while(true) {}")]
    [InlineData("nexus.writeFile('config.json', '{}');")]
    [InlineData("console.log({x:nexus.readConfig('undeclared')});")]
    public async Task FailureDoesNotBecomeLegacySuccess(string source)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(source,
            new {}, _ => throw new InvalidDataException(), _ => throw new InvalidDataException(), true, default));
    }
}
