using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Control;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class FrontendInstalledTests
{
    [Fact(DisplayName = "Installed Frontend Reads Wait For In Progress Recovery Publication")]
    public async Task WaitsForPublication()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        var catalog = fixture.Package.Catalog;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var publisher = Task.Run(() => catalog.Lease.PublishExecution(() =>
        {
            var blocked = catalog.Index.BlockUnknown("recovery_verification_in_progress");
            entered.Set();
            try { Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            finally { catalog.Index.PublishVerified(blocked.Revision, blocked.Revision + 1, blocked.Isolations); }
        }));
        Task<JsonElement>? read = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            read = Task.Run(async () => await frontend.ReadManifestAsync(default));
            Assert.True(SpinWait.SpinUntil(() => catalog.Lease.ExecutionPublicationCount == 2 || read.IsCompleted,
                TimeSpan.FromSeconds(5)));
            Assert.False(read.IsCompleted);
        }
        finally { release.Set(); await publisher; }
        try { Assert.Equal("fixture-plugin", (await read!).GetProperty("extensionId").GetString()); }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "Frozen Operation Rights Remain Available For Authorization While Damaged Installation Evidence Still Blocks Consumption")]
    [InlineData("metadata")]
    [InlineData("history-entry")]
    public async Task SeparatesDeclaredRights(string damage)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        Assert.Equal(ControlCapability.SettingsWrite, await frontend.CapabilityAsync("save-state", default));
        var owner = fixture.Package.Catalog;
        var installed = new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Roots)
            .ReadCurrent("fixture-plugin")!;
        var metadata = Path.Combine(owner.Instance.RuntimeRoot, installed.Metadata.Footprint.RelativePath);
        var bytes = File.ReadAllBytes(metadata);
        var added = Path.Combine(owner.Instance.RuntimeRoot, "journals", installed.Metadata.JournalId.ToString("D"), "9999999999.json");
        try
        {
            if (damage == "metadata") File.WriteAllText(metadata, "{}");
            else File.WriteAllText(added, "{}");
            Assert.Equal(ControlCapability.SettingsWrite, await frontend.CapabilityAsync("save-state", default));
            var entered = false;
            await Assert.ThrowsAnyAsync<IOException>(() => frontend.InvokeAsync("save-state", Guid.NewGuid(), "local", true,
                "{}"u8.ToArray(), () => entered = true, default).AsTask());
            Assert.False(entered);
        }
        finally
        {
            if (File.Exists(added)) File.Delete(added);
            File.WriteAllBytes(metadata, bytes);
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.ThrowsAnyAsync<Exception>(() => frontend.CapabilityAsync("save-state", default).AsTask());
    }

    [Fact(DisplayName = "An Installed Browser Module Reads Only Declared Original Assets And Uses The Actual Sdk Handler")]
    public async Task ReadsInstalledAssets()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        try
        {
            Assert.Equal("fixture-plugin", (await frontend.ReadManifestAsync(default)).GetProperty("extensionId").GetString());
            Assert.Equal("export const identity = 'fixture';\n", System.Text.Encoding.UTF8.GetString(await frontend.ReadAssetAsync("entry", default)));
            Assert.Contains(".fixture", System.Text.Encoding.UTF8.GetString(await frontend.ReadAssetAsync("0", default)));
            var assets = await frontend.ReadAssetsAsync(default);
            Assert.Equal(await frontend.ReadAssetAsync("entry", default), assets.Entry);
            Assert.Equal(await frontend.ReadAssetAsync("0", default), Assert.Single(assets.Styles));
            var entry = Path.Combine(fixture.Target, "frontend", "main.js");
            Assert.Throws<IOException>(() => File.WriteAllText(entry, "replaced"));
            var sibling = Path.Combine(fixture.Target, "unexpected.js");
            File.WriteAllText(sibling, "unexpected");
            try
            {
                await Assert.ThrowsAsync<IOException>(() => frontend.ReadAssetAsync("entry", default).AsTask());
                await Assert.ThrowsAsync<IOException>(() => frontend.ReadAssetsAsync(default).AsTask());
            }
            finally { File.Delete(sibling); }
            Assert.Equal("export const identity = 'fixture';\n", System.Text.Encoding.UTF8.GetString(await frontend.ReadAssetAsync("entry", default)));
            await Assert.ThrowsAsync<IOException>(() => frontend.ReadAssetAsync("../entry.dll", default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => frontend.ReadAssetAsync("1", default).AsTask());
            var clientId = Guid.NewGuid();
            var result = await frontend.InvokeAsync("read-state", null, "remote:actual", false,
                JsonSerializer.SerializeToUtf8Bytes(new { clientId = Guid.NewGuid() }), () => { }, default, clientId: clientId);
            using var json = JsonDocument.Parse(result.Content);
            Assert.Equal("remote:actual", json.RootElement.GetProperty("SubjectId").GetString());
            Assert.False(json.RootElement.GetProperty("IsLocal").GetBoolean());
            Assert.Equal(clientId, json.RootElement.GetProperty("ClientId").GetGuid());
            var anonymous = await frontend.InvokeAsync("read-state", null, "remote:actual", false,
                JsonSerializer.SerializeToUtf8Bytes(new { clientId }), () => { }, default);
            using var withoutClient = JsonDocument.Parse(anonymous.Content);
            Assert.Equal(JsonValueKind.Null, withoutClient.RootElement.GetProperty("ClientId").ValueKind);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        await Assert.ThrowsAnyAsync<Exception>(() => frontend.ReadAssetAsync("entry", default).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => frontend.ReadAssetsAsync(default).AsTask());
        using var released = File.Open(Path.Combine(fixture.Target, "frontend", "main.js"), FileMode.Open, FileAccess.Write, FileShare.Read);
    }

    [Theory(DisplayName = "Installed Browser Operations Preserve The Host Permission Categories And Require Write Identity")]
    [InlineData("save-state", ControlCapability.SettingsWrite)]
    [InlineData("reveal", ControlCapability.SecretRead)]
    [InlineData("login", ControlCapability.Desktop)]
    public async Task RequiresWriteIdentity(string operation, ControlCapability capability)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        try
        {
            Assert.Equal(capability, await frontend.CapabilityAsync(operation, default));
            await Assert.ThrowsAsync<IOException>(() => frontend.InvokeAsync(operation, null, "local", true,
                "{}"u8.ToArray(), () => { }, default).AsTask());
            var id = Guid.NewGuid();
            var result = await frontend.InvokeAsync(operation, id, "local", true, "{}"u8.ToArray(), () => { }, default);
            using var json = JsonDocument.Parse(result.Content);
            Assert.Equal(id, json.RootElement.GetProperty("OperationId").GetGuid());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "A Completed Plugin Callback Cannot Return After Caller Authorization Has Been Revoked")]
    public async Task RechecksCaller()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        try
        {
            var calls = 0;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => frontend.InvokeAsync("read-state", null, "local", true,
                "{}"u8.ToArray(), () => { if (++calls == 2) throw new UnauthorizedAccessException(); }, default).AsTask());
            Assert.Equal(2, calls);
            await Assert.ThrowsAnyAsync<Exception>(() => frontend.InvokeAsync("bad-json", null, "local", true,
                "{}"u8.ToArray(), () => { }, default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => frontend.CapabilityAsync("missing", default).AsTask());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "Stopping An Installed Scope Cancels And Drains Its Actual Frontend Callback Before Releasing Files")]
    public async Task DrainsStoppedCalls()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        var work = frontend.InvokeAsync("wait", null, "local", true, "{}"u8.ToArray(), () => { }, default).AsTask();
        try
        {
            using var entered = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(fixture.ActivationDirectory, "frontend-entered")))
                await Task.Delay(20, entered.Token);
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "frontend-entered")));
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "frontend-stopped")));
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "Retiring A Verified Client Cancels Its Actual Installed Callback Without Stopping The Shared Extension")]
    public async Task RetiresClientCalls()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        using var client = new CancellationTokenSource();
        var work = frontend.InvokeAsync("wait", null, "local", true, "{}"u8.ToArray(), () => { }, default,
            clientId: Guid.NewGuid(), clientLifetime: client.Token).AsTask();
        try
        {
            using var entered = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(fixture.ActivationDirectory, "frontend-entered"))) await Task.Delay(20, entered.Token);
            client.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "frontend-stopped")));
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
            _ = await frontend.InvokeAsync("read-state", null, "another:client", false, "{}"u8.ToArray(), () => { }, default);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "Installed Image Reads Use Only Read Operations Original Asset Identity And Current Caller")]
    public async Task ReadsScopedImages()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        try
        {
            var result = await frontend.ReadImageAsync("cover", "one", "remote:images", false, () => { }, default);
            Assert.Equal("image/png", result.ContentType); Assert.Equal(0x89, result.Content.Span[0]);
            await Assert.ThrowsAsync<IOException>(() => frontend.ReadImageAsync("save-state", "one", "local", true, () => { }, default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => frontend.ReadImageAsync("cover", "../one", "local", true, () => { }, default).AsTask());
            await Assert.ThrowsAsync<IOException>(() => frontend.ReadImageAsync("read-state", "one", "local", true, () => { }, default).AsTask());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => frontend.ReadImageAsync("cover", "one", "remote:images", false,
                () => throw new UnauthorizedAccessException(), default).AsTask());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        await Assert.ThrowsAnyAsync<Exception>(() => frontend.ReadImageAsync("cover", "one", "local", true, () => { }, default).AsTask());
    }

    [Theory(DisplayName = "A Frontend Callback Protects Its Exact Installation Bytes And Rechecks Added History And Current Sql Facts")]
    [InlineData("history-entry")]
    [InlineData("registry")]
    public async Task HoldsInstallationEvidence(string change)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend");
        var frontend = await OpenAsync(fixture);
        var owner = fixture.Package.Catalog;
        var installed = new ExtensionInstalledEvidence(owner.Instance, owner.Database, owner.Contracts, fixture.Roots)
            .ReadCurrent("fixture-plugin")!;
        var metadata = Path.Combine(owner.Instance.RuntimeRoot, installed.Metadata.Footprint.RelativePath);
        var bytes = File.ReadAllBytes(metadata);
        var journal = Path.Combine(owner.Instance.RuntimeRoot, "journals", installed.Metadata.JournalId.ToString("D"));
        var sibling = Path.Combine(journal, "9999999999.json");
        void ChangePhase(string phase)
        {
            using var connection = DatabaseConnections.Open(owner.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE main.operation_journals SET phase=$phase WHERE journal_id=$journal;";
            command.Parameters.AddWithValue("$journal", installed.Metadata.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$phase", phase);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        try
        {
            var calls = 0;
            await Assert.ThrowsAnyAsync<IOException>(() => frontend.InvokeAsync("read-state", null, "local", true,
                "{}"u8.ToArray(), () =>
                {
                    if (++calls != 2) return;
                    Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(metadata, bytes));
                    if (change == "history-entry") File.WriteAllText(sibling, "{}");
                    else ChangePhase("applied");
                }, default).AsTask());
        }
        finally
        {
            if (File.Exists(sibling)) File.Delete(sibling);
            if (change == "registry") ChangePhase("committed");
        }
        try
        {
            File.WriteAllBytes(metadata, bytes);
            _ = await frontend.InvokeAsync("read-state", null, "local", true, "{}"u8.ToArray(), () => { }, default);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    internal static async Task<InstalledFrontend> OpenAsync(InstallRuntimeFixture fixture)
    {
        var result = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(result.IsEnabled);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        var catalog = fixture.Package.Catalog;
        ExtensionProjectionPublication.PublishIfDirty(catalog.Instance, catalog.Database, catalog.Lease,
            catalog.Contracts, fixture.Roots, catalog.Resources, catalog.Index);
        return Assert.IsType<InstalledFrontend>(fixture.Activator.RequirePublishedRuntime("fixture-plugin").Frontend);
    }
}
