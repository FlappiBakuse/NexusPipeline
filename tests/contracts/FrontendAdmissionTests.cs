using System.Text;
using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class FrontendAdmissionTests
{
    private static string Manifest => """
        {"format":"nexus.extension.manifest","version":1,"extensionId":"fixture","artifactName":"Fixture",
        "displayName":"Fixture","packageVersion":"0.5.0","kind":"managed-code","minHostVersion":"0.18.0",
        "sdkVersion":"0.1.0","contractFingerprint":"__fingerprint__","capabilities":["ui.contribute"],"permissions":{},
        "managed":{"entryAssembly":"Fixture.dll","entryType":"Fixture.Extension"},
        "frontend":{"entry":"web/main.js","styles":[],"sdkVersion":"0.1.0"}}
        """.Replace("__fingerprint__", SdkIdentity.ContractFingerprint, StringComparison.Ordinal);

    [Fact(DisplayName = "Frontend Handshake Requires The Actual Sdk Build Fingerprint")]
    public void RequiresSdkFingerprint()
    {
        var admission = new FrontendAdmission(new ContractReader());
        var message = new FrontendHandshake
        {
            Format = "nexus.extension.frontend", Version = 1, ExtensionId = "fixture",
            SdkVersion = SdkIdentity.Version, ContractFingerprint = SdkIdentity.ContractFingerprint
        };
        var manifest = Encoding.UTF8.GetBytes(Manifest);
        var accepted = admission.ReadHandshake(manifest, JsonSerializer.SerializeToUtf8Bytes(message), "fixture");
        Assert.Equal(SdkIdentity.ContractFingerprint, accepted.ContractFingerprint);
        Assert.Throws<JsonException>(() => admission.ReadHandshake(manifest, JsonSerializer.SerializeToUtf8Bytes(
            message with { ContractFingerprint = new string('0', 64) }), "fixture"));
        Assert.Throws<JsonException>(() => admission.ReadHandshake(manifest, JsonSerializer.SerializeToUtf8Bytes(
            message with { ExtensionId = "foreign" }), "fixture"));
        var wrongManifest = System.Text.Json.Nodes.JsonNode.Parse(manifest)!;
        wrongManifest["contractFingerprint"] = new string('0', 64);
        Assert.Throws<JsonException>(() => admission.ReadHandshake(JsonSerializer.SerializeToUtf8Bytes(wrongManifest), JsonSerializer.SerializeToUtf8Bytes(message), "fixture"));
    }

    [Theory(DisplayName = "Legacy Frontend Descriptors And Native Bridges Cannot Pass The New Handshake")]
    [InlineData("{\"frontendApiVersion\":\"1.7\",\"pluginName\":\"Fixture\"}")]
    [InlineData("{\"type\":\"invoke\",\"method\":\"native.command\",\"args\":[]}")]
    public void RejectsLegacyBridges(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var original = bytes.ToArray();
        Assert.Throws<JsonException>(() => new FrontendAdmission(new ContractReader()).ReadHandshake(
            Encoding.UTF8.GetBytes(Manifest), bytes, "fixture"));
        Assert.Equal(original, bytes);
    }
}
