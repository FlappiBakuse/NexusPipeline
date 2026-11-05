using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Settings;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LivePreviewTests
{
    [Fact(DisplayName = "An Actual Host Serves The Installed Providers Png Only During Its Original Run And Closes The Grant")]
    public async Task RunsRemotePreview()
    {
        if (Environment.GetEnvironmentVariable("NEXUS_TEST_REMOTE_ACCESS") != "1")
            throw new IOException("NEXUS_TEST_REMOTE_ACCESS_required_for_lan_test");
        await using var installation = new InstallationCrashFixture("complete-exit", hostMedia: true);
        await installation.StartAsync();
        Assert.True((await installation.StopAsync()).CompleteJobStopped);
        var instance = installation.Instance;
        var executable = Path.Combine(installation.Roots.HostPayloadRoot, "NexusPipeline.exe");
        var marker = Path.Combine(Path.GetDirectoryName(instance.RuntimeRoot)!, "media-running");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Guid binding; long settingsRevision;
        using (var lease = InstanceLease.Acquire(instance, new()))
        {
            var database = StateDatabase.OpenExisting(instance, Path.Combine(Path.GetDirectoryName(instance.RuntimeRoot)!, "media-startup-inspection"),
                new JournalStartupAdmission(instance, new(), installation.Roots));
            var settingsStore = new HostSettingsStore(database); var savedSettings = settingsStore.Read();
            var enabled = new HostSettingsChange(savedSettings.Revision, savedSettings.Values with
            { Extensions = [new("fixture-plugin", true)], Service = savedSettings.Values.Service with { AllowRemoteAccess = true } });
            settingsStore.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", enabled), enabled, DateTimeOffset.UtcNow);
            settingsRevision = settingsStore.Read().Revision;
            binding = Seed(database, marker);
            var index = new StartupJournalRecovery(instance, new(), database, installation.Roots).Recover(lease);
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            var secrets = new SecretFieldService(instance, database, lease, resources, index, new());
            using var value = new SecretValue(token);
            await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(instance.InstanceId), 0,
                SecretDisposition.Set, value, default);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var item in new[] { "serve", "--runtime-root", instance.RuntimeRoot, "--port", "0" }) start.ArgumentList.Add(item);
        using var host = Process.Start(start)!;
        var errors = host.StandardError.ReadToEndAsync();
        try
        {
            var line = await host.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.False(string.IsNullOrEmpty(line), host.HasExited ? await errors : "Host not ready");
            using var ready = JsonDocument.Parse(line!);
            using var client = new HttpClient { BaseAddress = new(ready.RootElement.GetProperty("address").GetString()!), Timeout = TimeSpan.FromSeconds(15) };
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address)
                .First(item => item.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item));
            using var remote = new HttpClient { BaseAddress = new($"http://{address}:{client.BaseAddress.Port}"), Timeout = client.Timeout };
            remote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var extensionState = await client.GetStringAsync("/control/v1/extensions", deadline.Token);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(instance.RuntimeRoot)!, "host-extension-state.json"), extensionState);
            using (var extension = JsonDocument.Parse(extensionState))
            {
                Assert.Equal(JsonValueKind.String, extension.RootElement.GetProperty("items")[0].GetProperty("activationId").ValueKind);
                using var reconciled = await Post(remote, "/control/v1/extensions/reconcile", new
                { extensionId = "fixture-plugin", expectedInstalledRevision = extension.RootElement.GetProperty("items")[0].GetProperty("installedRevision").GetInt64(),
                    expectedSettingsRevision = settingsRevision }, deadline.Token);
                Assert.True(reconciled.RootElement.GetProperty("desiredEnabled").GetBoolean());
                Assert.Equal(JsonValueKind.String, reconciled.RootElement.GetProperty("activationId").ValueKind);
            }
            var operation = Guid.NewGuid();
            using var accepted = await Post(remote, $"/control/v1/execution/start/{operation:D}", new
            {
                format = "nexus.control.execution-selection", version = 1, target = new { kind = "binding", bindingId = binding },
                expectedCatalogRevision = 1, expectedSettingsRevision = settingsRevision
            }, deadline.Token);
            var run = accepted.RootElement.GetProperty("runId").GetGuid();
            var attempt = accepted.RootElement.GetProperty("attemptId").GetGuid();
            while (!File.Exists(marker + ".output-ready")) await Task.Delay(20, deadline.Token);
            using var opened = await OpenWhenRegistered(remote, new
            { format = "nexus.control.media-grant", version = 1, runId = run, attemptId = attempt, extensionId = "fixture-plugin", providerId = "frame" }, deadline.Token);
            var preview = opened.RootElement.GetProperty("previewId").GetGuid();
            var framePath = $"/control/v1/media/previews/{preview:D}/frame";
            using var frame = await remote.GetAsync(framePath, deadline.Token);
            Assert.Equal(200, (int)frame.StatusCode); Assert.Equal("image/png", frame.Content.Headers.ContentType!.MediaType);
            var bytes = await frame.Content.ReadAsByteArrayAsync(deadline.Token);
            Assert.Equal("1", Assert.Single(frame.Headers.GetValues("X-Image-Width")));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), Assert.Single(frame.Headers.GetValues("X-Content-Sha256")));
            Assert.True(bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            using var closed = await remote.PostAsync($"/control/v1/media/previews/{preview:D}/close", null, deadline.Token);
            Assert.Equal(200, (int)closed.StatusCode);
            using var stale = await remote.GetAsync(framePath, deadline.Token); Assert.Equal(404, (int)stale.StatusCode);
            using var cancelled = await Post(remote, $"/control/v1/execution/{operation:D}/cancel/{Guid.NewGuid():D}", new
            { format = "nexus.control.execution-cancellation", version = 1, runId = run, attemptId = attempt }, deadline.Token);
            while (true)
            {
                using var result = JsonDocument.Parse(await remote.GetStringAsync($"/control/v1/operations/{operation:D}", deadline.Token));
                if (result.RootElement.GetProperty("status").GetString() == "completed") break;
                await Task.Delay(50, deadline.Token);
            }
        }
        finally
        {
            if (!host.HasExited) host.Kill();
            await host.WaitForExitAsync();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(instance.RuntimeRoot)!, "host.stderr.log"), await errors);
        }
    }

    private static Guid Seed(StateDatabase database, string marker)
    {
        var worker = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Media fixture worker", AutomationRootKind.External, Path.GetDirectoryName(worker)!);
        var script = new AutomationScript(Guid.NewGuid(), 0, "Media fixture", AutomationKind.Generic, root.Id, null,
            new(root.Id, Path.GetFileName(worker)), ["process-output", "wait", marker], [], [], new(1, 0, 0),
            new(AutomationJudgementLanguage.Keywords, ["never-finish"], [], null));
        var user = new AutomationUser(Guid.NewGuid(), 0, "Media fixture", 0);
        var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, true, 0);
        var changes = new AutomationChanges(0, [root], [script], [user], [binding], [], [], []);
        new AutomationDefinitionStore(database).Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        return binding.Id;
    }

    private static async Task<JsonDocument> OpenWhenRegistered(HttpClient client, object value, CancellationToken cancellation)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.PostAsJsonAsync("/control/v1/media/previews", value, cancellation);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
            if ((int)response.StatusCode == 403 && waiting.Elapsed < TimeSpan.FromSeconds(10))
            { await Task.Delay(20, cancellation); continue; }
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");
            return JsonDocument.Parse(bytes);
        }
    }

    private static async Task<JsonDocument> Post(HttpClient client, string path, object value, CancellationToken cancellation)
    {
        using var response = await client.PostAsJsonAsync(path, value, cancellation);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellation);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");
        return JsonDocument.Parse(bytes);
    }
}
