using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerTestPaths
{
    internal static InstallerPaths? TryCurrent()
    {
        var requested = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        if (requested is null) return null;
        var marker = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(requested, ".nexus-installer-fixture"));
        return File.Exists(marker) ? Current() : null;
    }

    internal static InstallerPaths Current()
    {
        var requested = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("installer_fixture_required");
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(requested);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, ".nexus-installer-fixture"));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != 32) throw new IOException("installer_fixture_unverified");
        var bytes = new byte[32]; input.ReadExactly(bytes);
        var scope = System.Text.Encoding.ASCII.GetString(bytes);
        if (!Guid.TryParseExact(scope, "N", out _)) throw new IOException("installer_fixture_unverified");
        return new(Path.Combine(root, "installer-manager"), @"Software\NexusPipeline\Tests\Installer\" + scope);
    }
}
