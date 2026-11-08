using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Emulation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EmulatorSourceTests
{
    [Theory(DisplayName = "The Host Captures Real Scoped Tool And Configuration Bytes And Stops Its Actual Controlled CLI Family Before Returning Any Enumeration")]
    [InlineData("normal")]
    [InlineData("exit")]
    [InlineData("timeout")]
    public async Task RunsScopedInventory(string mode)
    {
        using var owner = new ExtensionCatalogFixture();
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "emulator 中文 inventory " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var worker = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        foreach (var file in JudgementInterpreterLease.Inspect(worker).RuntimeFiles.Select(item => item.RelativePath).Append(Path.GetFileName(worker)))
        {
            var target = Path.Combine(directory, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(worker)!, file), target);
        }
        var config = Path.Combine(directory, "emulator-command-fixture.json");
        File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(new { mode }));
        var authorized = true;
        await using var inventory = EmulatorInventoryScope.Prepare(owner.Instance, owner.Lease, owner.Resources, [directory],
            () => { if (!authorized) throw new UnauthorizedAccessException("fixture_emulator_revoked"); }, TimeSpan.FromSeconds(mode == "timeout" ? 2 : 30));
        var layout = new EmulatorLayout("arbitrary-engine", "Controlled fixture", [], [Path.GetFileName(worker)],
            [Path.GetFileName(worker)], [Path.GetFileName(config)], [], []) { EnumerationArguments = ["list2"] };
        var found = Assert.Single(await inventory.DiscoverAsync(layout, default));
        Assert.Equal(directory, found.RootPath); Assert.NotNull(found.Adb);
        Assert.Equal(EmulatorSourceLease.Identity(PhysicalObjectIdentity.Read(directory)), found.RootPhysicalId);
        var captured = Assert.Single(found.Configurations); Assert.Equal(File.ReadAllBytes(config), captured.Bytes.ToArray());
        Assert.Throws<IOException>(() => { using var denied = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
        var command = new EmulatorCommand(found.Control.Id, ["list2"]);
        var refused = await Assert.ThrowsAsync<EmulatorException>(() => inventory.EnumerateAsync(found, command with { Arguments = ["quit"] }, default).AsTask());
        Assert.Equal("emulator_enumeration_unbound", refused.Code);
        var result = await inventory.EnumerateAsync(found, command, default);
        Assert.Equal(mode == "timeout" ? "timeout" : "completed", result.State);
        Assert.Equal(mode == "timeout" ? null : mode == "exit" ? 17 : 0, result.ExitCode);
        if (mode == "normal") Assert.Contains("controlled fixture", result.StandardOutput);
        if (mode == "exit") Assert.Contains("controlled-cli-exit", result.StandardError);
        var marker = Path.Combine(directory, "emulator-command-started.json");
        using var started = JsonDocument.Parse(File.ReadAllBytes(marker));
        var pid = started.RootElement.GetProperty("processId").GetInt32();
        var created = started.RootElement.GetProperty("createdAt").GetDateTime();
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(new(pid, created, found.Control.Path, found.Control.Sha256, "", false)));
        var native = inventory.ReadEvidence(); Assert.Equal(mode == "timeout" ? 0 : 1, native.Count);
        foreach (var evidence in native)
        {
            Assert.Equal(found.Control.Sha256, evidence.Image.Sha256); Assert.Equal(0U, evidence.Stopped.ActiveProcesses);
            Assert.Empty(evidence.Stopped.LivingFamily); Assert.Contains(evidence.Stopped.AllFamily, process => process.ProcessId == pid);
        }
        authorized = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inventory.DiscoverAsync(layout, default).AsTask());
        await inventory.DisposeAsync();
        using var writable = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(captured.Bytes.Length, writable.Length);
    }
}
