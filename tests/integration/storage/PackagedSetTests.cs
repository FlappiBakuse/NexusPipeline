using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PackagedSetTests
{
    // Extension IDs from the release catalog and public capabilities from its source manifests.
    private static readonly IReadOnlyDictionary<string, string[]> PublishedCapabilities = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["baah"] = ["configuration.edit", "target.android", "task.adapt"],
        ["bettergi"] = ["configuration.edit", "task.adapt"],
        ["custom-wallpaper"] = ["ui.contribute"],
        ["emulator-support"] = ["emulator.provide"],
        ["game-activities"] = ["ui.contribute"],
        ["game-checkin"] = ["notification.submit", "schedule.register", "ui.contribute"],
        ["live-screenshot"] = ["media.capture", "ui.contribute"],
        ["maa-framework"] = ["configuration.edit", "execution.drive", "source.compile", "task.adapt", "ui.contribute"],
        ["maaend"] = ["configuration.edit", "target.android", "task.adapt"],
        ["maas"] = ["configuration.edit", "target.android", "task.adapt"],
        ["march7th"] = ["configuration.edit", "task.adapt"],
        ["oknte"] = ["configuration.edit", "task.adapt"],
        ["okww"] = ["configuration.edit", "task.adapt"],
        ["zzzonedragon"] = ["configuration.edit", "task.adapt"]
    };

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
            var ids = PublishedCapabilities.Keys.Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(ids, inputs.Select(input => input.Manifest["extensionId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            foreach (var input in inputs)
            {
                var id = input.Manifest["extensionId"]!.GetValue<string>();
                Assert.Equal(PublishedCapabilities[id], input.Manifest["capabilities"]!.AsArray()
                    .Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal));
            }
            await using var host = new FrontendHostFixture();
            await host.StartAsync(inputs[0], additional: inputs.Skip(1).ToArray(), application: application, bundleInstall: bundleInstall);
            using var first = Client(host);
            await ReadInstalledAsync(first, ids);
            await WaitForInstallationCleanupAsync(host, ids.Length);
            await SetEnabledAsync(first, ids, true);
            var active = await ReadInstalledAsync(first, ids, true);
            await ReadFrontendsAsync(first, active);
            await SetEnabledAsync(first, ids, false);
            await ReadInstalledAsync(first, ids, false);
            await host.ColdRestartAsync();
            File.Copy(Path.Combine(host.Root, "frontend-host.stderr.log"),
                Path.Combine(host.Root, "frontend-host-first.stderr.log"));
            using var second = Client(host);
            await ReadInstalledAsync(second, ids, false);
            await SetEnabledAsync(second, ids, true);
            var recovered = await ReadInstalledAsync(second, ids, true);
            foreach (var id in ids) Assert.NotEqual(active[id], recovered[id]);
            await ReadFrontendsAsync(second, recovered);
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
        Assert.Equal(ids, items.Select(item => item.GetProperty("extensionId").GetString()!).Order(StringComparer.Ordinal));
        var result = new Dictionary<string, Guid?>();
        foreach (var item in items)
        {
            Assert.True(item.GetProperty("isInstalled").GetBoolean());
            var id = item.GetProperty("extensionId").GetString()!;
            Assert.Equal(PublishedCapabilities[id], item.GetProperty("capabilities").EnumerateArray()
                .Select(value => value.GetString()).Order(StringComparer.Ordinal));
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

    private static async Task ReadFrontendsAsync(HttpClient client, IReadOnlyDictionary<string, Guid?> installed)
    {
        using var response = JsonDocument.Parse(await client.GetStringAsync("/control/v1/extensions/frontend"));
        var entries = response.RootElement.GetProperty("extensions").EnumerateArray().ToArray();
        Assert.Equal(PublishedCapabilities.Where(item => item.Value.Contains("ui.contribute", StringComparer.Ordinal))
            .Select(item => item.Key).Order(StringComparer.Ordinal), entries.Select(item =>
                item.GetProperty("manifest").GetProperty("extensionId").GetString()).Order(StringComparer.Ordinal));
        foreach (var entry in entries)
        {
            var id = entry.GetProperty("manifest").GetProperty("extensionId").GetString()!;
            Assert.Equal(installed[id], entry.GetProperty("activationId").GetGuid());
        }
    }

    private static async Task SetEnabledAsync(HttpClient client, string[] ids, bool enabled)
    {
        await WaitForRecoveryAsync(client);
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
            var extensionId = item.GetProperty("extensionId").GetString();
            using var reconciled = await client.PostAsJsonAsync("/control/v1/extensions/reconcile", new
            {
                extensionId,
                expectedInstalledRevision = item.GetProperty("installedRevision").GetInt64(), expectedSettingsRevision = revision
            });
            Assert.True(reconciled.IsSuccessStatusCode,
                $"{extensionId}, enabled={enabled}: {await reconciled.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task WaitForInstallationCleanupAsync(FrontendHostFixture host, int installedCount)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(host.Root, "runtime", "state", "runtime.db"),
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2
        }.ToString();
        var timer = Stopwatch.StartNew();
        long? committed = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM operation_journals WHERE operation_kind='fileCleanup' AND phase='committed';";
                committed = (long)command.ExecuteScalar()!;
                if (committed == installedCount) return;
                Assert.InRange(committed.Value, 0, installedCount);
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
            await Task.Delay(250);
        }
        throw new TimeoutException($"packaged_fixture_installation_cleanup_not_committed: {committed}/{installedCount}");
    }

    private static async Task WaitForRecoveryAsync(HttpClient client)
    {
        var timer = Stopwatch.StartNew();
        long? revision = null;
        var stableSince = TimeSpan.Zero;
        string? lastStatus = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(35))
        {
            using var response = await client.GetAsync("/control/v1/status");
            lastStatus = await response.Content.ReadAsStringAsync();
            using var status = JsonDocument.Parse(lastStatus);
            var root = status.RootElement;
            if (response.StatusCode == HttpStatusCode.InternalServerError && root.GetProperty("code").GetString() == "control_failed")
                revision = null;
            else
            {
                response.EnsureSuccessStatusCode();
                if (root.GetProperty("admission").GetString() != "ready" || root.GetProperty("pendingOperations").GetInt64() != 0)
                    revision = null;
                else if (revision != root.GetProperty("recoveryRevision").GetInt64())
                {
                    revision = root.GetProperty("recoveryRevision").GetInt64();
                    stableSince = timer.Elapsed;
                }
                // The installed-file sweeper first takes work on the next one-second tick.
                else if (timer.Elapsed - stableSince >= TimeSpan.FromMilliseconds(1500))
                    return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"packaged_fixture_recovery_not_ready: {lastStatus}");
    }
}
