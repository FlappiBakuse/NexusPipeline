using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class UpdateContractsTests
{
    [Theory(DisplayName = "Host Update Documents Have One Strict Current Shape Before Any Trust Or Process Effect")]
    [InlineData("update-feed")]
    [InlineData("update-payload-manifest")]
    [InlineData("update-receipt")]
    public void ValidatesCurrentDocuments(string name)
    {
        using var input = typeof(UpdateContractsTests).Assembly.GetManifestResourceStream("Examples." + name + ".json")!;
        using var copy = new MemoryStream(); input.CopyTo(copy);
        var bytes = copy.ToArray(); var reader = new ContractReader();
        using (reader.Read(bytes, name + ".schema.json")) { }
        var invalid = JsonNode.Parse(bytes)!.AsObject(); invalid["legacy"] = true;
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(invalid), name + ".schema.json"));
        invalid.Remove("legacy"); invalid["version"] = 17;
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(invalid), name + ".schema.json"));
        using var accepted = reader.Read(bytes, name + ".schema.json");
        Assert.Equal(1, accepted.RootElement.GetProperty("version").GetInt32());
    }

    [Theory(DisplayName = "Every Prepared Successor Requires The Same Complete Startup Binding")]
    [InlineData("candidate-prepared")]
    [InlineData("old-ready")]
    public void RequiresStartupBinding(string stage)
    {
        using var input = typeof(UpdateContractsTests).Assembly.GetManifestResourceStream("Examples.update-receipt.json")!;
        using var copy = new MemoryStream(); input.CopyTo(copy);
        var receipt = JsonNode.Parse(copy.ToArray())!.AsObject(); var reader = new ContractReader();
        receipt["stage"] = stage;
        receipt["process"] = new JsonObject
        {
            ["processId"] = 123, ["createdFileTime"] = 123456789, ["imagePath"] = "D:\\Nexus\\NexusPipeline.Host.exe",
            ["imageSha256"] = new string('a', 64), ["userSid"] = "S-1-5-21-1", ["isElevated"] = false
        };
        receipt["startup"] = new JsonObject
        {
            ["handoffId"] = receipt["correlationId"]!.DeepClone(), ["generationId"] = Guid.NewGuid(),
            ["jobName"] = "Local\\NexusWorker-" + Guid.NewGuid().ToString("N"), ["nonce"] = new string('b', 64),
            ["installedRoot"] = new JsonObject { ["volumeSerial"] = new string('c', 16), ["fileId"] = new string('d', 32) }
        };
        using (reader.Read(JsonSerializer.SerializeToUtf8Bytes(receipt), "update-receipt.schema.json")) { }
        foreach (var field in new[] { "handoffId", "generationId", "jobName", "nonce", "installedRoot" })
        {
            var incomplete = receipt.DeepClone().AsObject(); incomplete["startup"]!.AsObject().Remove(field);
            Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(incomplete), "update-receipt.schema.json"));
        }
        receipt.Remove("startup");
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(receipt), "update-receipt.schema.json"));
    }

    [Theory(DisplayName = "Current Payload Plan Schema Requires Exactly The Source Owned By Its Operation Kind")]
    [InlineData("updateApply")]
    [InlineData("extensionApply")]
    public void RequiresOwnedSource(string kind)
    {
        using var input = typeof(UpdateContractsTests).Assembly.GetManifestResourceStream("Examples.payload-install-plan.json")!;
        using var copy = new MemoryStream(); input.CopyTo(copy);
        var plan = JsonNode.Parse(copy.ToArray())!.AsObject(); var reader = new ContractReader();
        if (kind == "updateApply")
        {
            plan["operationKind"] = kind; plan["action"] = "upgrade";
            plan["before"] = plan["candidate"]!.DeepClone();
            plan["maintenanceSource"] = plan["extensionSource"]!.DeepClone();
            plan.Remove("extensionSource"); plan.Remove("extensionId");
        }
        using (reader.Read(JsonSerializer.SerializeToUtf8Bytes(plan), "payload-apply-plan.schema.json")) { }
        var required = kind == "updateApply" ? "maintenanceSource" : "extensionSource";
        var other = kind == "updateApply" ? "extensionSource" : "maintenanceSource";
        var original = plan[required]!.DeepClone(); plan.Remove(required);
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(plan), "payload-apply-plan.schema.json"));
        plan[required] = original; plan[other] = original.DeepClone();
        Assert.Throws<JsonException>(() => reader.Read(JsonSerializer.SerializeToUtf8Bytes(plan), "payload-apply-plan.schema.json"));
    }
}
