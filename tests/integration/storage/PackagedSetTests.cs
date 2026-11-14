using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PackagedSetTests
{
    [Fact]
    public Task RestoresInstalledSet() => RunAsync(null);

    [Fact]
    public Task InstallsBundledSet() => RunAsync(null, bundleInstall: true);

    [Fact]
    public async Task RestoresPublishedSet()
    {
        var receipt = Environment.GetEnvironmentVariable("NEXUS_PRODUCT_ASSEMBLY") ?? throw new IOException("product_assembly_required");
        var original = File.ReadAllBytes(receipt);
        using var assembly = JsonDocument.Parse(original);
        Assert.Equal("PASS_APPLICATION_ASSEMBLED", assembly.RootElement.GetProperty("status").GetString());
        Assert.Equal("test", assembly.RootElement.GetProperty("profile").GetString());
        var application = Path.GetDirectoryName(receipt)!;
        VerifyAssembly(application, assembly.RootElement);
        await RunAsync(application);
        Assert.Equal(original, File.ReadAllBytes(receipt));
        VerifyAssembly(application, assembly.RootElement);
    }

    private static void VerifyAssembly(string application, JsonElement receipt)
    {
        var records = receipt.GetProperty("files").EnumerateArray().ToArray();
        var owned = NexusPipeline.Platform.Storage.ApplicationTree.Capture(new(NexusPipeline.Platform.Storage.SnapshotBudget.Default), application, default);
        Assert.Equal(records.Length,
            owned.Entries.Count(entry => entry.Kind == NexusPipeline.Core.Configuration.FileObjectKind.File));
        foreach (var item in records)
        {
            var path = Path.GetFullPath(Path.Combine(application, item.GetProperty("path").GetString()!));
            Assert.StartsWith(Path.GetFullPath(application) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
                Assert.False((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            using var file = File.OpenRead(path);
            Assert.Equal(item.GetProperty("sizeBytes").GetInt64(), file.Length);
            Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(file)));
        }
    }

    private static async Task RunAsync(string? application, bool bundleInstall = false)
    {
        var inputs = new List<BuiltExtensionInputs>();
        try
        {
            using var packageSet = JsonDocument.Parse(File.ReadAllBytes(
                Environment.GetEnvironmentVariable("NEXUS_PACKAGED_SET") ?? throw new IOException("package_set_required")));
            Assert.Equal("PASS", packageSet.RootElement.GetProperty("status").GetString());
            foreach (var item in packageSet.RootElement.GetProperty("runs").EnumerateArray()
                .Where(item => item.GetProperty("label").GetString()!.EndsWith("-package", StringComparison.Ordinal)))
            {
                var path = item.GetProperty("receipt").GetString()!;
                var bytes = File.ReadAllBytes(path);
                Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                using var receipt = JsonDocument.Parse(bytes);
                inputs.Add(new(receipt.RootElement.GetProperty("extensionId").GetString()!, path, explicitPath: true));
            }
            Assert.Equal(14, inputs.Count);
            var ids = inputs.Select(input => input.Manifest["extensionId"]!.GetValue<string>()).Order().ToArray();
            Assert.Equal(14, ids.Distinct().Count());
            await using var host = new FrontendHostFixture();
            await host.StartAsync(inputs[0], additional: inputs.Skip(1).ToArray(), application: application, bundleInstall: bundleInstall);
            using var first = Client(host);
            await ReadInstalledAsync(first, ids);
            await SetEnabledAsync(first, ids, true);
            var active = await ReadInstalledAsync(first, ids, true);
            using var frontends = JsonDocument.Parse(await first.GetStringAsync("/control/v1/extensions/frontend"));
            Assert.Equal(5, frontends.RootElement.GetProperty("extensions").GetArrayLength());
            await SetEnabledAsync(first, ids, false);
            await ReadInstalledAsync(first, ids, false);
            await host.ColdRestartAsync();
            using var second = Client(host);
            await ReadInstalledAsync(second, ids, false);
            await SetEnabledAsync(second, ids, true);
            var recovered = await ReadInstalledAsync(second, ids, true);
            foreach (var id in ids) Assert.NotEqual(active[id], recovered[id]);
            foreach (var input in inputs) input.VerifyUnchanged();
        }
        finally { foreach (var input in inputs) input.Dispose(); }
    }

    private static HttpClient Client(FrontendHostFixture host)
    {
        var client = new HttpClient { BaseAddress = host.Address, Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        return client;
    }

    private static async Task<Dictionary<string, Guid?>> ReadInstalledAsync(HttpClient client, string[] ids, bool? enabled = null)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/control/v1/extensions"));
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(ids, items.Select(item => item.GetProperty("extensionId").GetString()!).Order().ToArray());
        var result = new Dictionary<string, Guid?>();
        foreach (var item in items)
        {
            Assert.True(item.GetProperty("isInstalled").GetBoolean());
            var id = item.GetProperty("extensionId").GetString()!;
            var activation = item.GetProperty("activationId");
            result.Add(id, activation.ValueKind == JsonValueKind.Null ? null : activation.GetGuid());
            if (enabled is null) continue;
            Assert.Equal(enabled, item.GetProperty("desiredEnabled").GetBoolean());
            if (enabled.Value)
            {
                Assert.True(result[id].HasValue, item.ToString());
                Assert.Equal(JsonValueKind.Null, item.GetProperty("unavailableReason").ValueKind);
            }
            else Assert.Null(result[id]);
        }
        return result;
    }

    private static async Task SetEnabledAsync(HttpClient client, string[] ids, bool enabled)
    {
        var settings = JsonNode.Parse(await client.GetStringAsync("/control/v1/settings"))!;
        var saved = settings["saved"]!;
        var values = saved["values"]!.DeepClone();
        values["extensions"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
            { ["extensionId"] = id, ["enabled"] = enabled }).ToArray());
        using var response = await client.PostAsJsonAsync("/control/v1/settings", new
        {
            format = "nexus.control.host-settings", version = 1, operationId = Guid.NewGuid(),
            expectedRevision = saved["revision"]!.GetValue<long>(), values
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var committed = JsonDocument.Parse(body);
        var revision = committed.RootElement.GetProperty("saved").GetProperty("revision").GetInt64();
        using var current = JsonDocument.Parse(await client.GetStringAsync("/control/v1/extensions"));
        foreach (var item in current.RootElement.GetProperty("items").EnumerateArray())
        {
            using var reconciled = await client.PostAsJsonAsync("/control/v1/extensions/reconcile", new
            {
                extensionId = item.GetProperty("extensionId").GetString(),
                expectedInstalledRevision = item.GetProperty("installedRevision").GetInt64(), expectedSettingsRevision = revision
            });
            Assert.True(reconciled.IsSuccessStatusCode, await reconciled.Content.ReadAsStringAsync());
        }
    }
}
