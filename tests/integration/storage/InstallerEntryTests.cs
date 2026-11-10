using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerEntryTests
{
    [Theory(DisplayName = "The Actual Maintenance Installer Entry Inspects New Destinations Without Creating Product Or Manager Data")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectsNewDestination(bool existing)
    {
        var root = CreateFixture(); var application = Path.Combine(root, "application");
        if (existing) Directory.CreateDirectory(application);
        var result = await Execute(root, ["installer-state", "inspect", "--root", application]);
        Assert.Equal(0, result.ExitCode);
        using var reply = JsonDocument.Parse(result.Output);
        Assert.Equal("nexus.installer.inspection", reply.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, reply.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("new", reply.RootElement.GetProperty("kind").GetString());
        Assert.Equal(application, reply.RootElement.GetProperty("root").GetString());
        Assert.Equal(existing, Directory.Exists(application));
        Assert.False(Directory.Exists(Path.Combine(root, "installer-manager")));
    }

    [Fact(DisplayName = "The Actual Maintenance Installer Entry Refuses Unknown Directory Bytes Before Any Product Mutation")]
    public async Task PreservesUnknownDestination()
    {
        var root = CreateFixture(); var application = Path.Combine(root, "application"); Directory.CreateDirectory(application);
        var file = Path.Combine(application, "user.txt"); File.WriteAllText(file, "Unknown retained user bytes");
        var identity = PhysicalObjectIdentity.Read(file);
        var result = await Execute(root, ["installer-state", "check-new", "--root", application]);
        Assert.Equal(1, result.ExitCode);
        using var diagnostic = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "entry.stderr.log")));
        Assert.Equal("installer_entry_rejected", diagnostic.RootElement.GetProperty("code").GetString());
        Assert.Equal("installer_ownership_unverified", diagnostic.RootElement.GetProperty("reason").GetString());
        Assert.Equal("InstallationDestination.Inspect", diagnostic.RootElement.GetProperty("origin").GetString());
        Assert.True(diagnostic.RootElement.GetProperty("HResult").GetInt32() < 0);
        Assert.Equal("Unknown retained user bytes", File.ReadAllText(file));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(file));
        Assert.False(Directory.Exists(Path.Combine(root, "installer-manager")));
    }

    [Fact(DisplayName = "A Test Installer Entry Has No Production Manager Fallback When Its Explicit Fixture Is Missing")]
    public async Task RequiresExplicitFixture()
    {
        var root = CreateFixture(); File.Delete(Path.Combine(root, ".nexus-installer-fixture"));
        var result = await Execute(root, ["installer-state", "check-new", "--root", Path.Combine(root, "application")]);
        Assert.Equal(1, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "installer-manager")));
    }

    [Theory(DisplayName = "The Actual Installer Entry Sends Sealed Active And Retained Instances To Their Existing Instance Transition")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresOwnedTransition(bool retained)
    {
        using var fixture = new InstallerIdentityFixture(installerContext: true);
        var identity = fixture.Identity with { State = retained ? "retained-data" : "active" };
        fixture.Save(InstallationIdentityCodec.Seal(identity));
        var before = File.ReadAllBytes(fixture.SealPath);
        var result = await Execute(fixture.Root, ["installer-state", "check-new", "--root", fixture.Application]);
        Assert.Equal(10, result.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.SealPath));
        Assert.Equal(identity, fixture.Anchor.Read(fixture.Application));
    }

    [Fact(DisplayName = "The Actual Installer Entry Rejects Unanchored Native Registration Before Admitting An Empty Destination")]
    public async Task PreservesNativeRegistration()
    {
        var root = CreateFixture(); var id = File.ReadAllText(Path.Combine(root, ".nexus-installer-fixture"));
        var path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.Tests." + id + "_is1";
        using (var key = Registry.CurrentUser.CreateSubKey(path)) key.SetValue("InstallLocation", "Unanchored retained value");
        try
        {
            var result = await Execute(root, ["installer-state", "check-new", "--root", Path.Combine(root, "application")]);
            Assert.Equal(1, result.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(root, "application")));
            using var key = Registry.CurrentUser.OpenSubKey(path);
            Assert.Equal("Unanchored retained value", key!.GetValue("InstallLocation"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path); }
    }

    [Theory(DisplayName = "The Actual Installer Entry Rejects Unknown Commands And Extra Arguments")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnknownArguments(bool extra)
    {
        var root = CreateFixture();
        var command = extra ? new[] { "installer-state", "inspect", "--root", root, "--unknown" } : ["installer-state", "unknown"];
        var result = await Execute(root, command);
        Assert.Equal(2, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "installer-manager")));
    }

    private static string CreateFixture()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nexus-installer-fixture"), Guid.NewGuid().ToString("N"));
        return root;
    }

    private static async Task<(int ExitCode, string Output)> Execute(string root, string[] command)
    {
        var executable = Environment.GetEnvironmentVariable("NEXUS_TEST_INSTALLER_EXE") ?? throw new IOException("installer_fixture_required");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command) start.ArgumentList.Add(argument);
        start.Environment["NEXUS_TEST_ROOT"] = root;
        using var process = Process.Start(start) ?? throw new IOException("installer_fixture_start_failed");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        var stdout = await output; var stderr = await error;
        File.WriteAllText(Path.Combine(root, "entry.stdout.log"), stdout);
        File.WriteAllText(Path.Combine(root, "entry.stderr.log"), stderr);
        File.WriteAllText(Path.Combine(root, "entry-command.json"), JsonSerializer.Serialize(new { executable, command, process.ExitCode }));
        return (process.ExitCode, stdout);
    }
}
