using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Updates;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class UpdateRecipeTests
{
    private static JsonObject Recipe()
    {
        string hash = new('a', 64);
        string[] paths = ["NexusPipeline.exe", UpdateRecipe.UpdateEntry, "README.md", ApplicationPayload.ManifestPath,
            ApplicationPayload.DesktopEntry, ApplicationPayload.DesktopArchive,
            "resources/desktop/resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node"];
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["packageKind"] = "portable", ["product"] = "NexusPipeline",
            ["rid"] = "win-x64", ["installationGeneration"] = "g0170", ["base"] = null,
            ["target"] = new JsonObject { ["version"] = "0.17.4", ["buildId"] = hash,
                ["frontendHash"] = hash, ["payloadManifestSha256"] = hash },
            ["requirements"] = new JsonObject { ["readerProtocol"] = 2, ["routeId"] = "g0170-no-store-change-v1",
                ["requiredCapabilities"] = new JsonArray(UpdateRecipe.Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) },
            ["includedFiles"] = new JsonArray(paths.Select(path => (JsonNode?)new JsonObject {
                ["path"] = path, ["sizeBytes"] = 10, ["sha256"] = hash }).ToArray()),
            ["reusedFiles"] = new JsonArray(), ["removedFiles"] = new JsonArray(), ["bundledFiles"] = new JsonArray(),
            ["manualCover"] = new JsonObject { ["allowed"] = false, ["minimumSourceVersion"] = "0.17.3",
                ["exactBaseRequired"] = false, ["requiresPostStartReconciliation"] = true, ["reason"] = "use-setup-or-new-portable-directory" },
        };
    }

    private static UpdateRecipe Parse(JsonObject value, string kind = "portable")
        => UpdateRecipe.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()), "0.17.4", kind);

    [Fact]
    public void FullPackageRequiresAnExactCompleteApplicationInventory()
    {
        var input = Recipe();
        var actual = Parse(input);
        Assert.Equal("0.17.4", actual.Version);
        Assert.Null(actual.Base);
        Assert.Empty(actual.ReusedFiles);
        Assert.False(actual.ManualCoverAllowed);
        ((JsonArray)input["includedFiles"]!).RemoveAt(1);
        Assert.Throws<InvalidDataException>(() => Parse(input));
    }

    [Fact]
    public void UnknownPathsOverlappingSetsAndDuplicateKeysAreRejected()
    {
        foreach (string path in new[] { "config/user.json", "../escape", "NexusPipeline Uninstall.exe", "resources/desktop/CON" })
        {
            var input = Recipe();
            input["includedFiles"]![0]!["path"] = path;
            Assert.Throws<InvalidDataException>(() => Parse(input));
        }
        var overlap = Recipe();
        ((JsonArray)overlap["reusedFiles"]!).Add(overlap["includedFiles"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => Parse(overlap));
        string duplicate = Recipe().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => UpdateRecipe.Parse(Encoding.UTF8.GetBytes(duplicate), "0.17.4", "portable"));
    }

    [Fact]
    public void ManualCoverRequiresExactBaseAndNoApplicationRemoval()
    {
        var input = Recipe();
        input["packageKind"] = "update";
        input["manualCover"]!["allowed"] = true;
        input["manualCover"]!["exactBaseRequired"] = true;
        Assert.Throws<InvalidDataException>(() => Parse(input, "update"));
        input["base"] = new JsonObject { ["releaseTag"] = "v0.17.3", ["releaseId"] = 1, ["assetId"] = 2,
            ["assetSha256"] = new string('b', 64), ["payloadManifestSha256"] = new string('c', 64) };
        Assert.True(Parse(input, "update").ManualCoverAllowed);
        ((JsonArray)input["removedFiles"]!).Add(new JsonObject { ["path"] = "resources/desktop/old.dll",
            ["sizeBytes"] = 1, ["sha256"] = new string('d', 64) });
        Assert.Throws<InvalidDataException>(() => Parse(input, "update"));
    }
}
