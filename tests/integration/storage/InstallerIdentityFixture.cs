using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallerIdentityFixture : IDisposable
{
    internal string Root { get; }
    internal string Application { get; }
    internal string Manager { get; }
    internal string RegistryPath { get; }
    internal string SealPath => Path.Combine(Manager, InstallationAnchor.IdentityFile);
    internal string WorkerPath => Path.Combine(Manager, InstallationAnchor.WorkerFile);
    internal string EnginePath => Path.Combine(Application, InstallationAnchor.EngineEntry);
    internal InstallationIdentity Identity { get; }
    internal InstallationAnchor Anchor { get; }

    internal InstallerIdentityFixture(bool installerContext = false)
    {
        var id = Guid.NewGuid().ToString("N");
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-identity-" + id);
        Application = Path.Combine(Root, "application"); Manager = Path.Combine(Root, installerContext ? "installer-manager" : "manager");
        RegistryPath = @"Software\NexusPipeline\Tests\Installer\" + id;
        Directory.CreateDirectory(Application); Directory.CreateDirectory(Manager);
        var runtime = new InstanceRoot(new ContractReader()).OpenOrCreate(Path.Combine(Application, "runtime"));
        File.WriteAllBytes(WorkerPath, "Controlled identity worker bytes"u8.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(EnginePath)!);
        File.WriteAllBytes(EnginePath, "Controlled native engine bytes"u8.ToArray());
        Identity = new("nexus.installation", 1, "g0180", Guid.NewGuid(), InstallationIdentityCodec.UserSid,
            Application, PhysicalObjectIdentity.Read(Application), runtime.InstanceId, PhysicalObjectIdentity.Read(runtime.RuntimeRoot),
            new('c', 64), "0.18.0", "active", DateTimeOffset.UtcNow, FileProof(WorkerPath), FileProof(EnginePath));
        Anchor = new(Manager, RegistryPath);
        Save(InstallationIdentityCodec.Seal(Identity));
        if (installerContext) File.WriteAllText(Path.Combine(Root, ".nexus-installer-fixture"), id);
    }

    internal void Save(byte[] bytes)
    {
        File.WriteAllBytes(SealPath, bytes);
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue("ApplicationRoot", Application, RegistryValueKind.String);
        key.SetValue("InstallationId", Identity.InstallationId.ToString("D"), RegistryValueKind.String);
        key.SetValue("IdentitySha256", Digest(bytes), RegistryValueKind.String);
    }

    internal byte[] Forge(string fault)
    {
        var value = JsonSerializer.SerializeToNode(Identity, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        switch (fault)
        {
            case "format": value["format"] = "other.installation"; break;
            case "version": value["version"] = 2; break;
            case "generation": value["generation"] = "g0176"; break;
            case "user": value["userSid"] = "S-1-5-18"; break;
            case "extra": value["foreign"] = true; break;
            case "missing": value.Remove("runtimeId"); break;
            case "state": value["state"] = "pending-legacy"; break;
            case "product": value["productVersion"] = "0.17.6"; break;
            case "native": value["nativeEngine"]!["sha256"] = "unverified"; break;
            case "numeric-string": value["version"] = "1"; break;
            default: throw new ArgumentException(fault);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return WindowsDataProtection.Transform(bytes, Encoding.UTF8.GetBytes("nexus.installation/g0180/1"), protect: true);
    }

    internal static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static InstallerFile FileProof(string path) => new(new FileInfo(path).Length, Digest(File.ReadAllBytes(path)), PhysicalObjectIdentity.Read(path));

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
}
