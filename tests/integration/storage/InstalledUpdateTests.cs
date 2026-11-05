using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledUpdateTests
{
    [Fact(DisplayName = "A Real Setup Installed Host Commits A Derived Full Payload And Its Bound Installation Metadata Then Uninstalls With Data Retained")]
    public Task CommitsInstalledIdentity() => VerifyAsync(failure: false);

    [Fact(DisplayName = "A Real Installed Candidate Startup Failure Restores The Original Host Ownership And Account Before Native Keep Uninstall")]
    public Task RestoresInstalledIdentity() => VerifyAsync(failure: true);

    private static async Task VerifyAsync(bool failure)
    {
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var inputs = new PublishedUpdateInputs();
        using var installed = new InstalledMaintenanceScope(inputs);
        var seal = Path.Combine(installed.Paths.ManagerRoot, InstallationAnchor.IdentityFile);
        var originalBytes = File.ReadAllBytes(seal); var originalObject = PhysicalObjectIdentity.Read(seal);
        using (var fixture = new MaintenanceBinaryFixture(failCandidateStartup: failure, published: inputs, installed: installed))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
            Assert.NotNull(source.Installation);
            var old = fixture.Start();
            var line = await old.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.False(string.IsNullOrWhiteSpace(line), old.HasExited ? await fixture.HostErrors : "Installed Host not ready");
            using var client = new HttpClient { BaseAddress = fixture.Address, Timeout = TimeSpan.FromSeconds(60) };
            var account = await CreateAccountAsync(client, deadline.Token);
            await fixture.AwaitSchedulingAsync(deadline.Token);
            var path = $"control/v1/maintenance/operations/{fixture.Plan.OperationId:D}";
            using var started = await client.PostAsync(path + "/start", null, deadline.Token);
            Assert.True((int)started.StatusCode == 202, await started.Content.ReadAsStringAsync(deadline.Token));
            using var helper = await fixture.AwaitHelperAsync(deadline.Token);
            RecordHandoff(fixture, "helper-observed", old, helper);
            await old.WaitForExitAsync(deadline.Token); Assert.Equal(0, old.ExitCode);
            RecordHandoff(fixture, "parent-exited", old, helper);
            await helper.WaitForExitAsync(deadline.Token); Assert.Equal(0, helper.ExitCode);
            RecordHandoff(fixture, "helper-exited", old, helper);
            using var successor = fixture.ObserveSuccessor(old.Id);
            HttpResponseMessage? response = null;
            while (response is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.False(successor.HasExited, "Installed successor exited before readiness");
                try { response = await client.GetAsync("control/v1/capabilities", deadline.Token); }
                catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
            }
            using (response) Assert.Equal(200, (int)response.StatusCode);
            using var completed = await client.GetAsync(path, deadline.Token);
            using var operation = JsonDocument.Parse(await completed.Content.ReadAsStringAsync(deadline.Token));
            Assert.Equal(failure ? "originalRestored" : "updated", operation.RootElement.GetProperty("outcome").GetString());
            using var automation = JsonDocument.Parse(await client.GetStringAsync("control/v1/automation", deadline.Token));
            Assert.Contains(automation.RootElement.GetProperty("bundle").GetProperty("users").EnumerateArray(),
                user => user.GetProperty("id").GetGuid() == account);
            var identity = new InstallationAnchor(installed.Paths.ManagerRoot, installed.Paths.RegistryKey).Read(installed.Target)!;
            Assert.Equal(installed.Original.InstallationId, identity.InstallationId);
            Assert.Equal(installed.Original.RuntimeId, identity.RuntimeId);
            Assert.Equal(installed.Original.CreatedAt, identity.CreatedAt);
            Assert.Equal(installed.Original.Worker, identity.Worker);
            Assert.Equal(installed.Original.NativeEngine, identity.NativeEngine);
            Assert.False(Directory.Exists(Path.Combine(installed.Paths.ManagerRoot, "metadata-checkpoint")));
            if (failure)
            {
                Assert.Equal(installed.Original, identity);
                Assert.Equal(originalBytes, File.ReadAllBytes(seal)); Assert.Equal(originalObject, PhysicalObjectIdentity.Read(seal));
                inputs.VerifyPayload(installed.Target);
            }
            else
            {
                Assert.NotEqual(installed.Original.ManifestSha256, identity.ManifestSha256);
                Assert.Equal(source.PayloadManifest.Sha256, identity.ManifestSha256);
                Assert.NotEqual(originalObject, PhysicalObjectIdentity.Read(seal));
                Assert.Equal("Controlled installed maintenance target", File.ReadAllText(Path.Combine(installed.Target, "README.md")));
            }
            File.WriteAllText(Path.Combine(installed.Root, "owned-update.json"), JsonSerializer.Serialize(new
            {
                scope = "Actual current published programs and native Setup; signed derived Test archive changes README or candidate startup only",
                failure, account, original = installed.Original, identity, source, fixture.Plan.OperationId,
                helperExitCode = helper.ExitCode, outcome = operation.RootElement.Clone()
            }));
        }
        installed.Uninstall();
        Assert.True(File.Exists(Path.Combine(installed.Target, "runtime", "instance.json")));
        inputs.VerifyUnchanged();
    }

    private static void RecordHandoff(MaintenanceBinaryFixture fixture, string stage,
        System.Diagnostics.Process parent, System.Diagnostics.Process helper)
    {
        var members = new List<object>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        using (process)
        {
            try
            {
                if (process.HasExited || process.MainModule?.FileName is not { } path ||
                    !path.StartsWith(fixture.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                members.Add(new { process.Id, process.ProcessName, path });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        File.WriteAllText(Path.Combine(fixture.Root, "handoff-" + stage + ".json"), JsonSerializer.Serialize(new
        {
            stage, fixture.Plan.OperationId, parentId = parent.Id, parentExited = parent.HasExited,
            helperId = helper.Id, helperExited = helper.HasExited, helperExit = helper.HasExited ? helper.ExitCode : (int?)null,
            members, recordedAt = DateTimeOffset.UtcNow
        }));
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, CancellationToken cancellation)
    {
        using var automation = JsonDocument.Parse(await client.GetStringAsync("control/v1/automation", cancellation));
        var id = Guid.NewGuid();
        var request = new JsonObject
        {
            ["format"] = "nexus.control.automation-change", ["version"] = 1, ["operationId"] = Guid.NewGuid().ToString("D"),
            ["expectedCatalogRevision"] = automation.RootElement.GetProperty("catalogRevision").GetInt64(),
            ["users"] = new JsonArray(new JsonObject { ["expectedRevision"] = 0, ["value"] = new JsonObject
            { ["id"] = id.ToString("D"), ["name"] = "Installed maintenance acceptance", ["order"] = 0, ["notes"] = "Generated read-only acceptance account" } })
        };
        foreach (var field in new[] { "roots", "scripts", "bindings", "queues", "schedules", "removed" }) request[field] = new JsonArray();
        using var response = await client.PostAsJsonAsync("control/v1/automation/change", request, cancellation);
        Assert.True((int)response.StatusCode == 200, await response.Content.ReadAsStringAsync(cancellation));
        return id;
    }
}
