using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BundledStartupTests
{
    [Fact]
    public async Task PreservesRemovedDefault()
    {
        using var emulator = new BuiltExtensionInputs("emulator-support", "NEXUS_FORMAL_EMULATOR_PACKAGE_RECEIPT");
        using var screenshot = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        using var wallpaper = new BuiltExtensionInputs("custom-wallpaper", "NEXUS_PACKAGED_WALLPAPER_RECEIPT");
        await using var host = new FrontendHostFixture();
        await host.StartAsync(emulator, additional: [screenshot, wallpaper], defaultBundle: true);
        using var client = Client(host);
        using var state = JsonDocument.Parse(await client.GetStringAsync("/control/v1/extensions"));
        var items = state.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "emulator-support", "live-screenshot" }, items.Select(item => item.GetProperty("extensionId").GetString()!).Order());
        Assert.All(items, item => Assert.True(item.GetProperty("isInstalled").GetBoolean()));
        var installedDates = Dates(state, "live-screenshot");
        Assert.Equal(installedDates.Created, installedDates.Updated);
        Assert.True(installedDates.Created <= DateTimeOffset.UtcNow);
        var emulatorDates = Dates(state, "emulator-support");
        var selected = Assert.Single(items, item => item.GetProperty("extensionId").GetString() == "live-screenshot");
        using var settings = JsonDocument.Parse(await client.GetStringAsync("/control/v1/settings"));
        var operation = Guid.NewGuid();
        using var accepted = await client.PostAsJsonAsync("/control/v1/extensions/apply", new
        {
            format = "nexus.control.extension-apply", version = 1, operationId = operation,
            command = new { action = "remove", extensionId = "live-screenshot",
                expectedInstalledRevision = selected.GetProperty("installedRevision").GetInt64(),
                expectedSettingsRevision = settings.RootElement.GetProperty("saved").GetProperty("revision").GetInt64(),
                catalog = (object?)null, candidate = (object?)null }
        });
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (true)
        {
            using var result = JsonDocument.Parse(await client.GetStringAsync("/control/v1/extensions/operations/" + operation, deadline.Token));
            if (result.RootElement.GetProperty("outcome").GetString() == "removed") break;
            await Task.Delay(50, deadline.Token);
        }
        await host.ColdRestartAsync();
        using var restarted = Client(host);
        using var after = JsonDocument.Parse(await restarted.GetStringAsync("/control/v1/extensions"));
        var removed = Assert.Single(after.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("extensionId").GetString() == "live-screenshot");
        Assert.False(removed.GetProperty("isInstalled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("metadata").ValueKind);
        Assert.Equal(emulatorDates, Dates(after, "emulator-support"));
        Assert.DoesNotContain(after.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("extensionId").GetString() == "custom-wallpaper");
        await host.ReinstallBundleAsync();
        using var restoredClient = Client(host);
        using var restored = JsonDocument.Parse(await restoredClient.GetStringAsync("/control/v1/extensions"));
        var restoredItem = Assert.Single(restored.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("extensionId").GetString() == "live-screenshot");
        Assert.True(restoredItem.GetProperty("isInstalled").GetBoolean());
        Assert.True(restoredItem.GetProperty("installedRevision").GetInt64() > removed.GetProperty("installedRevision").GetInt64());
        var restoredDates = Dates(restored, "live-screenshot");
        Assert.Equal(installedDates.Created, restoredDates.Created);
        Assert.True(restoredDates.Updated > installedDates.Updated);
        emulator.VerifyUnchanged(); screenshot.VerifyUnchanged(); wallpaper.VerifyUnchanged();
    }

    [Fact]
    public async Task RefreshesExplicitBundle()
    {
        using var emulator = new BuiltExtensionInputs("emulator-support", "NEXUS_FORMAL_EMULATOR_PACKAGE_RECEIPT");
        using var screenshot = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        await using var host = new FrontendHostFixture();
        await host.StartAsync(emulator, additional: [screenshot], defaultBundle: true);
        using var client = Client(host);
        using var before = JsonDocument.Parse(await client.GetStringAsync("/control/v1/extensions"));
        var revision = Revision(before);
        var originalDates = Dates(before, "emulator-support");
        var bundle = Path.Combine(host.Root, "installation-bundle");
        var catalog = JsonNode.Parse(File.ReadAllBytes(Path.Combine(bundle, "catalog.json")))!;
        var entry = catalog["entries"]!.AsArray().Single(item => item!["extensionId"]!.GetValue<string>() == "emulator-support")!;
        var archive = Path.Combine(bundle, entry["artifactName"]!.GetValue<string>() + "-" + entry["packageVersion"]!.GetValue<string>() + ".zip");
        byte[] manifestBytes;
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var manifestEntry = zip.GetEntry("manifest.json")!;
            JsonNode manifest;
            using (var stream = manifestEntry.Open()) manifest = JsonNode.Parse(stream)!;
            manifest["packageVersion"] = "0.5.1"; manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            manifestEntry.Delete();
            using (var stream = zip.CreateEntry("manifest.json").Open()) stream.Write(manifestBytes);
            using var writer = new StreamWriter(zip.CreateEntry("fixture-refresh.txt").Open()); writer.Write("Controlled refresh payload");
        }
        entry["packageVersion"] = "0.5.1";
        var updatedArchive = Path.Combine(bundle, entry["artifactName"]!.GetValue<string>() + "-0.5.1.zip");
        File.Move(archive, updatedArchive);
        archive = updatedArchive;
        entry["artifact"]!["url"] = new Uri(ExtensionCatalogFixture.Artifacts,
            entry["artifactName"]!.GetValue<string>() + "/" + Path.GetFileName(archive)).AbsoluteUri;
        entry["manifestSha256"] = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        entry["artifact"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive)));
        entry["artifact"]!["sizeBytes"] = new FileInfo(archive).Length;
        using var signer = new ExtensionCatalogFixture(useHostTestTrust: true);
        var signed = signer.Signed(2, edit: value => value["entries"] = catalog["entries"]!.DeepClone());
        File.WriteAllBytes(Path.Combine(bundle, "index.json"), signed.Pointer);
        File.WriteAllBytes(Path.Combine(bundle, "catalog.json"), signed.Catalog);
        File.WriteAllBytes(Path.Combine(bundle, "catalog.sig.json"), signed.Signature);
        await host.ReinstallBundleAsync();
        using var updatedClient = Client(host);
        using var updated = JsonDocument.Parse(await updatedClient.GetStringAsync("/control/v1/extensions"));
        Assert.Equal(revision + 1, Revision(updated));
        var updatedDates = Dates(updated, "emulator-support");
        Assert.Equal(originalDates.Created, updatedDates.Created);
        Assert.True(updatedDates.Updated > originalDates.Updated);
        Assert.Equal("Controlled refresh payload", File.ReadAllText(Path.Combine(host.Root, "runtime", "extensions", "emulator-support", "fixture-refresh.txt")));
        await host.ReinstallBundleAsync();
        using var replayClient = Client(host);
        using var replay = JsonDocument.Parse(await replayClient.GetStringAsync("/control/v1/extensions"));
        Assert.Equal(Revision(updated), Revision(replay));
        var catalogOnly = signer.Signed(3, edit: value => value["entries"] = catalog["entries"]!.DeepClone());
        File.WriteAllBytes(Path.Combine(bundle, "index.json"), catalogOnly.Pointer);
        File.WriteAllBytes(Path.Combine(bundle, "catalog.json"), catalogOnly.Catalog);
        File.WriteAllBytes(Path.Combine(bundle, "catalog.sig.json"), catalogOnly.Signature);
        await host.ReinstallBundleAsync();
        using var catalogClient = Client(host);
        using var refreshedCatalog = JsonDocument.Parse(await catalogClient.GetStringAsync("/control/v1/extensions/catalog/stable"));
        Assert.Equal(3, refreshedCatalog.RootElement.GetProperty("identity").GetProperty("sequence").GetInt64());
        using var retained = JsonDocument.Parse(await catalogClient.GetStringAsync("/control/v1/extensions"));
        Assert.Equal(Revision(updated), Revision(retained));
        Assert.Equal(updatedDates, Dates(retained, "emulator-support"));
        emulator.VerifyUnchanged(); screenshot.VerifyUnchanged();
    }

    private static long Revision(JsonDocument state) => state.RootElement.GetProperty("items").EnumerateArray()
        .Single(item => item.GetProperty("extensionId").GetString() == "emulator-support").GetProperty("installedRevision").GetInt64();

    private static (DateTimeOffset Created, DateTimeOffset Updated) Dates(JsonDocument state, string extensionId)
    {
        var metadata = state.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("extensionId").GetString() == extensionId).GetProperty("metadata");
        return (metadata.GetProperty("createdAt").GetDateTimeOffset(), metadata.GetProperty("updatedAt").GetDateTimeOffset());
    }

    private static HttpClient Client(FrontendHostFixture host)
    {
        var client = new HttpClient { BaseAddress = host.Address, Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        return client;
    }
}
