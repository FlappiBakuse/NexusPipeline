using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Emulation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EmulatorInstalledTests
{
    [Theory(DisplayName = "The Original Emulator ZIP Registers Its Actual Vendor Provider And Drains Escaped Sessions Without Running A Vendor Program")]
    [InlineData("LdPlayer")]
    [InlineData("Nox")]
    [InlineData("BlueStacks")]
    public async Task RunsInstalledProvider(string vendor)
    {
        using var input = new SpecializedPackageInputs("emulator-support", "NEXUS_FORMAL_EMULATOR_PACKAGE_RECEIPT", "managed-code");
        using var fixture = new InstallRuntimeFixture(originalManifest: input.Manifest, originalManifestBytes: input.ManifestBytes,
            packageArchive: input.Archive);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        var inventory = new EmulatorInventoryFixture(vendor);
        IEmulatorSession? escaped = null;
        IEmulatorProvider? retained = null;
        try
        {
            _ = await fixture.Activator.Emulators.UseAsync("emulator-support", "vendors", async (provider, current) =>
            {
                retained = provider;
                var ignored = await provider.DiscoverAsync(inventory, false, default);
                Assert.Equal("notApplicable", ignored.State); Assert.Equal(0, inventory.Reads);
                var found = await provider.DiscoverAsync(inventory, true, default);
                Assert.Equal("discovered", found.State);
                var source = Assert.Single(found.Sources); var instance = Assert.Single(source.Enumeration.Instances);
                Assert.Equal(vendor, source.Installation.Vendor); Assert.NotNull(instance.TargetId);
                Assert.Equal(2, found.Diagnostics.Count(item => item.Code == "emulator_vendor_not_installed"));
                var endpoint = "127.0.0.1:" + Assert.Single(instance.CandidatePorts, port => port == inventory.Port);
                var target = provider.Select(found.Sources, instance.TargetId, endpoint, true);
                Assert.Equal(instance.TargetId, target.Id);
                var unsupported = Assert.ThrowsAny<EmulatorException>(() => provider.Select(found.Sources, target.Id, endpoint, false));
                Assert.Equal("emulator_script_android_unsupported", unsupported.Code);
                var host = new EmulatorHostFixture(target);
                escaped = provider.Open(host, target);
                var device = await escaped.StartAsync(default);
                Assert.Equal(target.Id, device.TargetId); Assert.Equal(endpoint, device.Endpoint);
                Assert.Equal("captured", (await escaped.CaptureAsync(default)).State);
                await escaped.CloseAsync(true, default); await escaped.CancelAsync(default);
                Assert.Equal(vendor == "BlueStacks" ? 1 : 2, host.Commands.Count);
                Assert.All(host.Commands, command => Assert.Equal(target.Installation.Control.Id, command.ToolId));
                Assert.Equal(vendor == "BlueStacks" ? "--instance" : "launch", host.Commands[0].Arguments[0]);
                Assert.Equal(1, host.Cancelled); current(); return true;
            }, default);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
        await Assert.ThrowsAsync<IOException>(async () => await fixture.Activator.Emulators.UseAsync("emulator-support", "vendors",
            (_, _) => ValueTask.FromResult(true), default));
        Assert.NotNull(escaped); Assert.NotNull(retained);
        await Assert.ThrowsAsync<IOException>(() => escaped.CaptureAsync(default).AsTask());
        var reads = inventory.Reads;
        await Assert.ThrowsAsync<IOException>(() => retained.DiscoverAsync(inventory, true, default).AsTask());
        Assert.Equal(reads, inventory.Reads); input.VerifyUnchanged();
    }
}
