using System.Runtime.InteropServices;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class ShortcutJobTests
{
    [Theory(DisplayName = "Short And Long Image Detached Jobs Revalidate Complete Frozen Shortcut Bytes And Object Identity")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsDetachedShortcut(bool longImage)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "lnk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, ApplicationTree.HostEntry);
        File.WriteAllBytes(target, "Controlled detached shortcut target"u8.ToArray());
        var scope = Guid.NewGuid().ToString("N");
        var paths = new InstallerPaths(Path.Combine(root, "installer-manager"), @"Software\NexusPipeline\Tests\Installer\" + scope);
        Directory.CreateDirectory(paths.GroupRoot);
        var path = Path.Combine(paths.GroupRoot, "NexusPipeline v0.18.0.lnk");
        object? shell = null; object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic link = shortcut = ((dynamic)shell!).CreateShortcut(path);
            link.TargetPath = target; link.Arguments = ""; link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
        var bytes = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        var source = Path.Combine(root, "metadata.json");
        File.WriteAllBytes(source, InstallerSnapshotCodec.Encode(InstallerMetadataInventory.Capture(root, paths)));
        using var origin = MaintenanceStartupOrigin.CaptureUnjobbedProcess();
        using var job = new MaintenanceStartupJob(origin, _ => { });
        var report = Path.Combine(root, "shortcut-read.json");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        if (longImage) fixture = CopyImage(root, fixture);
        var child = job.Start(fixture, ["installer-shortcut-native", source, root, paths.ManagerRoot, paths.RegistryKey, report], Path.GetPathRoot(fixture)!,
            Guid.NewGuid(), Guid.NewGuid(), job.RequirePersistingRoot);
        var exit = await job.AwaitRootAsync(child, TimeSpan.FromSeconds(25));
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
        using var result = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.True(exit == 0, result.RootElement.GetRawText());
        Assert.True(result.RootElement.GetProperty("accepted").GetBoolean());
        Assert.True(result.RootElement.GetProperty("isThreadPoolThread").GetBoolean());
        Assert.Equal(1U, job.GetAccounting().TotalProcesses);
        Assert.Equal(0U, job.GetAccounting().ActiveProcesses);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
    }

    private static string CopyImage(string root, string fixture)
    {
        var image = Path.Combine(root, "image");
        while (Path.Combine(image, Path.GetFileName(fixture)).Length < 320)
            image = Path.Combine(image, "native-image-" + new string('x', 48));
        var copies = new List<object>();
        var source = Path.GetDirectoryName(fixture)!;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (Path.GetExtension(file) is ".pdb" or ".xml" || parts[0] == "runtimes" && parts[1] != "win-x64") continue;
            var target = Path.Combine(image, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            copies.Add(new { path = target, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))),
                sizeBytes = new FileInfo(target).Length, identity = PhysicalObjectIdentity.Read(target) });
        }
        File.WriteAllText(Path.Combine(root, "fixture-copies.json"), JsonSerializer.Serialize(copies));
        return Path.Combine(image, Path.GetFileName(fixture));
    }
}
