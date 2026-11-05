using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class RetryContractTests
{
    [Fact(DisplayName = "The New Provider Retry Declaration Requires Both Original Failure And Interruption Outcomes")]
    public void RequiresRetryOutcomes()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Declaration());
        var before = bytes.ToArray();
        using var document = new ContractReader().Read(bytes, "execution-provider-retry.schema.json");
        Assert.Equal("workerAndUpstream", document.RootElement.GetProperty("scope").GetString());
        Assert.Equal(new[] { "failed", "interrupted" }, document.RootElement.GetProperty("outcomes")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(before, bytes);
    }

    [Theory(DisplayName = "Incomplete Or Unknown Provider Retry Declarations Fail Without Changing Their Input")]
    [InlineData("safety")]
    [InlineData("scope")]
    [InlineData("idempotencyProtocol")]
    [InlineData("outcomes")]
    [InlineData("duplicate-outcome")]
    [InlineData("missing-interrupted")]
    [InlineData("unknown-field")]
    public void RejectsUnknownDeclarations(string damage)
    {
        var declaration = Declaration();
        switch (damage)
        {
            case "safety": declaration["safety"] = "unknown"; break;
            case "scope": declaration["scope"] = "workerOnly"; break;
            case "idempotencyProtocol": declaration["idempotencyProtocol"] = "stable-run-binding/2"; break;
            case "outcomes": declaration.Remove("outcomes"); break;
            case "duplicate-outcome": declaration["outcomes"] = new JsonArray("failed", "failed"); break;
            case "missing-interrupted": declaration["outcomes"] = new JsonArray("failed"); break;
            case "unknown-field": declaration["approvedByHost"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(declaration); var original = bytes.ToArray();
        Assert.Throws<JsonException>(() => new ContractReader().Read(bytes, "execution-provider-retry.schema.json"));
        Assert.Equal(original, bytes);
    }

    private static JsonObject Declaration() => new()
    {
        ["safety"] = "safe", ["scope"] = "workerAndUpstream", ["idempotencyProtocol"] = "stable-run-binding/1",
        ["outcomes"] = new JsonArray("failed", "interrupted")
    };
}
