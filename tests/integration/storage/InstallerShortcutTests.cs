using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerShortcutTests
{
    [Theory(DisplayName = "Native Shortcut Ownership Reads Preserve Bytes And Reject Foreign Targets Arguments And Invalid Files")]
    [InlineData("valid")]
    [InlineData("target")]
    [InlineData("arguments")]
    [InlineData("bytes")]
    public void ReadsWithoutMutation(string mode)
    {
        var scope = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "lnk-" + scope[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nexus-installer-fixture"), scope);
        var target = Path.Combine(root, ApplicationTree.HostEntry);
        File.WriteAllBytes(target, "Controlled shortcut target"u8.ToArray());
        var foreign = Path.Combine(root, "foreign.exe");
        File.WriteAllBytes(foreign, "Controlled foreign target"u8.ToArray());
        var path = Path.Combine(root, "owned.lnk");
        Create(path, mode == "target" ? foreign : target, mode == "arguments" ? "--unexpected" : "");
        if (mode == "bytes") File.WriteAllBytes(path, "Unrecognized shortcut bytes"u8.ToArray());
        var before = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = Record.Exception(() => InstallerShortcut.Require(path, root));
        if (mode == "valid") Assert.Null(failure);
        else Assert.True(failure is IOException or COMException, failure?.ToString() ?? "Foreign shortcut was accepted");
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.Equal("Controlled shortcut target"u8.ToArray(), File.ReadAllBytes(target));
    }

    [Theory(DisplayName = "Frozen Shortcut Validation Requires Original Bytes And Physical Identity And Preserves Changed Inputs")]
    [InlineData("current")]
    [InlineData("arguments")]
    [InlineData("object")]
    public void VerifiesFrozenShortcut(string mode)
    {
        var scope = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "fl-" + scope[..8]);
        var application = Path.Combine(root, "app");
        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(root, ".nexus-installer-fixture"), scope);
        var target = Path.Combine(application, ApplicationTree.HostEntry);
        File.WriteAllBytes(target, "Controlled frozen shortcut target"u8.ToArray());
        var paths = new InstallerPaths(Path.Combine(root, "installer-manager"), @"Software\NexusPipeline\Tests\Installer\" + scope);
        Directory.CreateDirectory(paths.GroupRoot);
        var path = Path.Combine(paths.GroupRoot, "NexusPipeline v0.18.0.lnk");
        Create(path, target, "");
        var expected = InstallerMetadataInventory.Capture(application, paths);
        if (mode == "arguments") Create(path, target, "--unexpected");
        if (mode == "object") { File.Move(path, path + ".old"); File.Copy(path + ".old", path); }
        var bytes = File.ReadAllBytes(path);
        var identity = PhysicalObjectIdentity.Read(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (mode == "current") Assert.True(InstallerMetadataInventory.Equal(expected, InstallerMetadataInventory.CaptureFrozen(expected, paths)));
        else Assert.Throws<IOException>(() => InstallerMetadataInventory.CaptureFrozen(expected, paths));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
    }

    private static void Create(string path, string target, string arguments)
    {
        object? shell = null; object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic link = shortcut = ((dynamic)shell!).CreateShortcut(path);
            link.TargetPath = target; link.Arguments = arguments; link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
