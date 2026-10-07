using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Updates;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class ApplicationBuildIdentityTests
{
    private const string ExpectedId = "58e6649438efd8f699f8a04291cabcf3718b38721c7b970ef454537894896d71";

    [Fact]
    public void SharedVectorProducesTheSameCanonicalBytesAndIdentity()
    {
        using JsonDocument inputs = JsonDocument.Parse(Fixture("Inputs"));
        Assert.Equal(Fixture("Canonical"), ApplicationBuildIdentity.CanonicalBytes(inputs.RootElement));
        Assert.Equal(ExpectedId, ApplicationBuildIdentity.ComputeBuildId(inputs.RootElement));
        ApplicationBuildIdentity record = ApplicationBuildIdentity.Parse(Fixture("Record"));
        Assert.Equal(ExpectedId, record.BuildId);
        Assert.Equal("0.17.0", record.ProductVersion);
        Assert.Equal("g0170", record.InstallationGeneration);
        Assert.Equal(new string('d', 64), record.FrontendHash);
    }

    [Fact]
    public void ProfiledSharedVectorRejectsMissingUnknownAndMistypedProfileInputs()
    {
        using JsonDocument inputs = JsonDocument.Parse(Fixture("ProfileInputs"));
        ApplicationBuildIdentity record = ApplicationBuildIdentity.Parse(Fixture("ProfileRecord"));
        Assert.Equal(Fixture("ProfileCanonical"), ApplicationBuildIdentity.CanonicalBytes(inputs.RootElement));
        Assert.Equal(record.BuildId, ApplicationBuildIdentity.ComputeBuildId(inputs.RootElement));
        foreach (string key in new[] { "runtimeProfileId", "runtimeProfileSha256", "runtimeInventorySha256" })
        {
            JsonObject changed = JsonNode.Parse(Fixture("ProfileInputs"))!.AsObject();
            changed[key] = key == "runtimeProfileId" ? "another-profile" : new string('1', 64);
            using JsonDocument different = JsonDocument.Parse(changed.ToJsonString());
            Assert.NotEqual(record.BuildId, ApplicationBuildIdentity.ComputeBuildId(different.RootElement));
            changed.Remove(key);
            using JsonDocument missing = JsonDocument.Parse(changed.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.ComputeBuildId(missing.RootElement));
        }
        foreach (JsonNode? version in new JsonNode?[] { JsonValue.Create("2"), JsonValue.Create(3), JsonValue.Create(true) })
        {
            JsonObject changed = JsonNode.Parse(Fixture("ProfileInputs"))!.AsObject();
            changed["schemaVersion"] = version;
            using JsonDocument invalid = JsonDocument.Parse(changed.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.ComputeBuildId(invalid.RootElement));
        }
    }

    [Fact]
    public void ReorderedKeysAndPrintableEscapesKeepCanonicalMeaning()
    {
        JsonObject original = JsonNode.Parse(Fixture("Inputs"))!.AsObject();
        var reversed = new JsonObject();
        foreach ((string key, JsonNode? value) in original.Reverse()) reversed[key] = value!.DeepClone();
        using JsonDocument document = JsonDocument.Parse(reversed.ToJsonString());
        Assert.Equal(ExpectedId, ApplicationBuildIdentity.ComputeBuildId(document.RootElement));
        using JsonDocument escaped = JsonDocument.Parse("{\"z\":\"/<>& \\\" \\\\\",\"a\":1}");
        Assert.Equal("{\"a\":1,\"z\":\"/<>& \\\" \\\\\"}", Encoding.UTF8.GetString(ApplicationBuildIdentity.CanonicalBytes(escaped.RootElement)));
    }

    [Fact]
    public void EveryMutableFrozenInputChangesTheIdentity()
    {
        JsonObject original = JsonNode.Parse(Fixture("Inputs"))!.AsObject();
        foreach (string key in new[] { "productVersion", "sourceSha", "sourceTreeSha", "partnerSha", "workflowSha",
                     "frontendHash", "frontendPackageLockSha256", "desktopPackageLockSha256", "electronVersion", "electronArchiveSha256" })
        {
            JsonObject changed = original.DeepClone().AsObject();
            changed[key] = key.EndsWith("Version", StringComparison.Ordinal) ? "0.17.1" : new string('1', key.EndsWith("Sha", StringComparison.Ordinal) ? 40 : 64);
            using JsonDocument document = JsonDocument.Parse(changed.ToJsonString());
            Assert.NotEqual(ExpectedId, ApplicationBuildIdentity.ComputeBuildId(document.RootElement));
        }
        foreach (string key in original["toolchain"]!.AsObject().Select(pair => pair.Key))
        {
            JsonObject changed = original.DeepClone().AsObject();
            changed["toolchain"]![key] = key.EndsWith("Sha256", StringComparison.Ordinal) ? new string('1', 64) : "24.0.1";
            using JsonDocument document = JsonDocument.Parse(changed.ToJsonString());
            Assert.NotEqual(ExpectedId, ApplicationBuildIdentity.ComputeBuildId(document.RootElement));
        }
    }

    [Fact]
    public void InvalidFieldsNumbersAndCharactersAreRejected()
    {
        foreach (string json in new[] { "{\"a\":1,\"a\":2}", "{\"a\":{\"b\":1,\"b\":2}}", "{\"a\":true}",
                     "{\"a\":null}", "{\"a\":[]}", "{\"a\":-0}", "{\"a\":1.0}", "{\"a\":1e0}",
                     "{\"a\":9007199254740992}", "{\"a\":\"中文\"}", "{\"a\":\"\\n\"}", "{\"中文\":1}" })
        {
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.CanonicalBytes(document.RootElement));
        }
        foreach (string key in new[] { "schemaVersion", "bootstrapProtocolVersion", "generation", "rid", "sourceSha", "productVersion", "electronVersion" })
        {
            JsonObject changed = JsonNode.Parse(Fixture("Inputs"))!.AsObject();
            changed[key] = "invalid";
            using JsonDocument document = JsonDocument.Parse(changed.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.ComputeBuildId(document.RootElement));
        }
        JsonObject extra = JsonNode.Parse(Fixture("Inputs"))!.AsObject();
        extra["unknown"] = "unused";
        using JsonDocument unknown = JsonDocument.Parse(extra.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.ComputeBuildId(unknown.RootElement));
        JsonObject oversizedVersion = JsonNode.Parse(Fixture("Inputs"))!.AsObject();
        oversizedVersion["productVersion"] = new string('1', 5000) + ".0.0";
        using JsonDocument huge = JsonDocument.Parse(oversizedVersion.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.ComputeBuildId(huge.RootElement));
        Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.Parse(new byte[ApplicationBuildIdentity.MaxBytes + 1]));
    }

    [Fact]
    public void RecordProjectionCannotOverrideItsFrozenInputs()
    {
        foreach (string key in new[] { "product", "productVersion", "installationGeneration", "rid", "buildId", "frontendHash" })
        {
            JsonObject record = JsonNode.Parse(Fixture("Record"))!.AsObject();
            record[key] = "wrong";
            Assert.Throws<InvalidDataException>(() => ApplicationBuildIdentity.Parse(Encoding.UTF8.GetBytes(record.ToJsonString())));
        }
    }

    private static byte[] Fixture(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NexusPipeline.Tests.BuildIdentity." + name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
