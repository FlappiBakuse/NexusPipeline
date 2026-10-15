using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Modules.Updates;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (int index = 0; index < args.Length; index += 2)
    if (index + 1 >= args.Length || !options.TryAdd(args[index], Path.GetFullPath(args[index + 1]))) throw new ArgumentException("Invalid arguments");
if (!options.Keys.Order().SequenceEqual(new[] { "--application", "--baseline", "--output", "--packages" }.Order())) throw new ArgumentException("Explicit application, baseline, packages and output required");
string application = options["--application"], baseline = options["--baseline"], output = options["--output"], packages = options["--packages"];
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Diagnostic output exists");
Directory.CreateDirectory(output);
var target = ApplicationPayload.Validate(application);
if (target.ProductVersion != "0.17.4") throw new InvalidDataException("Diagnostic requires the v0.17.4 target");
_ = ApplicationPayload.Validate(baseline, "0.17.3");
var cases = new List<object>();
VerifiedManualAdoption? proof = null;
using var http = new HttpClient(new OfficialFixture(packages));
foreach (bool absent in new[] { false, true })
    foreach (string stop in new[] { "none", "prepared", "helper", "identity", "anchor" })
        await Case($"adoption-{stop}-helper-absent-{absent}", async (root, owner) =>
        {
            string helper = Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe");
            if (absent) File.Delete(helper);
            var pending = InstallationOwnership.PendingManualAdoption(root, target.ProductVersion)!;
            Assert(pending.State == "manual-adoption-pending", "pending ownership");
            proof ??= await ManualAdoptionProof.VerifyAsync(http, root, pending, target.ProductVersion, Path.Combine(root, "proof"), default);
            try
            {
                InstallationOwnership.CommitManualAdoption(root, pending, proof, phase => { if (phase == stop) throw new InjectedStop(); });
                Assert(stop == "none", "fault was not reached");
            }
            catch (InjectedStop) { }
            var recovered = InstallationOwnership.Read(root, true)!;
            Assert(recovered.Version == target.ProductVersion && recovered.InstanceId == owner.InstanceId, "recovered identity");
            Assert(Hash(helper) == Hash(Path.Combine(root, "NexusPipeline.exe")), "recovered helper");
            Assert(!File.Exists(Path.Combine(InstallationOwnership.ManagerDirectory, "manual-adoption.protected")), "checkpoint cleanup");
            Assert(InstallationOwnership.PendingManualAdoption(root, target.ProductVersion) is null, "maintenance restored");
            if (stop == "none")
            {
                InstallationOwnership.Uninstall(root, false);
                Assert(!File.Exists(Path.Combine(root, UpdateRecipe.UpdateEntry)), "adopted update entry uninstalled");
                Assert(InstallationOwnership.Read(root)!.State == "retained-data", "uninstall retains user data ownership");
            }
        });
foreach (string stop in new[] { "maintenance-prepared", "maintenance-identity", "maintenance-anchor" })
    await Case(stop, (root, owner) =>
    {
        try
        {
            InstallationOwnership.PendingManualAdoption(root, target.ProductVersion,
                phase => { if (phase == stop) throw new InjectedStop(); });
            throw new InvalidDataException("Maintenance fault was not reached");
        }
        catch (InjectedStop) { }
        var pending = InstallationOwnership.Read(root)!;
        Assert(pending.State == "manual-adoption-pending" && pending.Version == owner.Version, "pending state recovered");
        Assert(pending.PayloadFiles.SequenceEqual(owner.PayloadFiles), "old ownership bytes not adopted");
        Assert(!File.Exists(Path.Combine(InstallationOwnership.ManagerDirectory, "manual-maintenance.protected")), "maintenance checkpoint cleanup");
        Assert(Hash(Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe"))
            == owner.PayloadFiles.Single(file => file.Path == "NexusPipeline.exe").Sha256, "old helper retained");
        Throws(() => InstallationOwnership.Read(root, true));
        Throws(() => InstallationOwnership.Uninstall(root, true));
        return Task.CompletedTask;
    });
await Case("untrusted-helper-preserved", (root, owner) =>
{
    string helper = Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe");
    File.WriteAllText(helper, "untrusted fixture bytes");
    Throws(() => InstallationOwnership.PendingManualAdoption(root, target.ProductVersion));
    Assert(File.ReadAllText(helper) == "untrusted fixture bytes", "unknown helper preserved");
    Assert(InstallationOwnership.Read(root, true)!.Version == owner.Version, "old owner retained");
    return Task.CompletedTask;
});
await Case("offline-keeps-original-ownership", async (root, owner) =>
{
    using var offline = new HttpClient(new OfflineFixture());
    var pending = InstallationOwnership.PendingManualAdoption(root, target.ProductVersion)!;
    await ThrowsAsync(() => ManualAdoptionProof.VerifyAsync(offline, root, pending, target.ProductVersion, Path.Combine(root, "proof"), default));
    Assert(InstallationOwnership.Read(root)!.Version == owner.Version, "offline owner retained");
    Assert(InstallationOwnership.Read(root)!.State == "manual-adoption-pending", "offline maintenance remains suspended");
    Throws(() => InstallationOwnership.Uninstall(root, false));
});
await Case("restored-original-payload-resumes-maintenance-after-interruption", (root, owner) =>
{
    _ = InstallationOwnership.PendingManualAdoption(root, target.ProductVersion);
    Throws(() => InstallationOwnership.RestoreManualBaseline(root));
    CopyApplication(baseline, root);
    try { InstallationOwnership.RestoreManualBaseline(root, stage => { if (stage == "maintenance-identity") throw new InjectedStop(); }); }
    catch (InjectedStop) { }
    var restored = InstallationOwnership.Read(root, true)!;
    Assert(restored.Version == owner.Version && restored.State == "active", "exact original payload restores maintenance");
    return Task.CompletedTask;
});
await Case("missing-common-files-repaired-after-independent-proof", async (root, owner) =>
{
    foreach (string file in new[] { "README.md", UpdateRecipe.UpdateEntry }) File.Delete(Path.Combine(root, file));
    var pending = InstallationOwnership.PendingManualAdoption(root, target.ProductVersion)!;
    using var offline = new HttpClient(new OfflineFixture());
    await ThrowsAsync(() => ManualAdoptionProof.VerifyAsync(offline, root, pending, target.ProductVersion, Path.Combine(root, "offline-proof"), default));
    Assert(!File.Exists(Path.Combine(root, UpdateRecipe.UpdateEntry)), "offline helper absence retained");
    Assert(!File.Exists(Path.Combine(root, "README.md")), "offline readme absence retained");
    var verified = await ManualAdoptionProof.VerifyAsync(http, root, pending, target.ProductVersion, Path.Combine(root, "proof"), default);
    foreach (string file in new[] { "README.md", UpdateRecipe.UpdateEntry })
        Assert(Hash(Path.Combine(root, file)) == Hash(Path.Combine(application, file)), "official missing file restored");
    Assert(InstallationOwnership.Read(root)!.State == "manual-adoption-pending", "repair does not prematurely commit ownership");
    InstallationOwnership.CommitManualAdoption(root, pending, verified);
    Assert(InstallationOwnership.Read(root, true)!.Version == target.ProductVersion, "repaired adoption committed");
});
await Case("untrusted-common-entry-preserved", async (root, owner) =>
{
    string entry = Path.Combine(root, UpdateRecipe.UpdateEntry);
    File.WriteAllText(entry, "untrusted entry");
    Throws(() => InstallationOwnership.PendingManualAdoption(root, target.ProductVersion));
    var pending = InstallationOwnership.Read(root)!;
    Assert(pending.State == "manual-adoption-pending", "untrusted entry suspends maintenance");
    await ThrowsAsync(() => ManualAdoptionProof.VerifyAsync(http, root, pending, target.ProductVersion, Path.Combine(root, "proof"), default));
    Assert(File.ReadAllText(entry) == "untrusted entry", "untrusted entry retained");
});
await Case("self-described-modified-payload-rejected", async (root, owner) =>
{
    string readme = Path.Combine(root, "README.md"), manifest = Path.Combine(root, ApplicationPayload.ManifestPath);
    File.AppendAllText(readme, "modified fixture bytes");
    var data = JsonNode.Parse(File.ReadAllText(manifest))!;
    var item = data["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == "README.md")!;
    item["sha256"] = Hash(readme); item["sizeBytes"] = new FileInfo(readme).Length;
    File.WriteAllText(manifest, data.ToJsonString());
    string changed = Hash(manifest);
    _ = ApplicationPayload.Validate(root, target.ProductVersion, installed: true);
    await ThrowsAsync(() => ManualAdoptionProof.VerifyAsync(http, root, owner, target.ProductVersion, Path.Combine(root, "proof"), default));
    Assert(Hash(manifest) == changed, "untrusted payload preserved");
    Assert(InstallationOwnership.Read(root, true)!.Version == owner.Version, "untrusted payload not adopted");
});
if (cases.Count != 19) throw new InvalidDataException("Missing adoption scenarios");
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { status = "PASS", cases, passed = cases.Count,
    failed = 0, skipped = 0, classification = "LOCAL_DIAGNOSTIC", candidateExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Manual adoption: {cases.Count} scenarios PASS");

async Task Case(string name, Func<string, InstallerInstanceIdentity, Task> action)
{
    string id = Guid.NewGuid().ToString("N"), fixture = Path.Combine(output, name), root = Path.Combine(fixture, "app");
    Directory.CreateDirectory(fixture);
    File.WriteAllText(Path.Combine(fixture, ".nxp-installer-lab"), id);
    Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", id);
    Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", fixture);
    bool passed = false;
    try
    {
        CopyApplication(baseline, root);
        var inventory = ApplicationPayload.Files(root).Select(file => new InstalledPayloadFile(file.Path, file.Sha256))
            .Append(new(ApplicationPayload.ManifestPath, Hash(Path.Combine(root, ApplicationPayload.ManifestPath)))).ToArray();
        string inventoryFile = Path.Combine(fixture, "inventory.json");
        File.WriteAllText(inventoryFile, JsonSerializer.Serialize(inventory));
        InstallationOwnership.Register(root, "0.17.3", inventoryFile);
        var owner = InstallationOwnership.Read(root, true)!;
        CopyApplication(application, root);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        string user = Path.Combine(root, "data", "retained.bin");
        File.WriteAllBytes(user, [0, 255, 13, 10, 42]);
        string original = Hash(user);
        await action(root, owner);
        Assert(Hash(user) == original, "user bytes retained");
        cases.Add(new { name, status = "PASS", instanceId = id });
        Console.WriteLine("PASS " + name);
        passed = true;
    }
    finally
    {
        if (passed)
        {
            Registry.CurrentUser.DeleteSubKeyTree(InstallationOwnership.CurrentRegistryPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(InstallationOwnership.CurrentUninstallRegistryPath, false);
            InstallationOwnership.RequireLinkFree(fixture);
            Directory.Delete(fixture, true);
        }
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE", null);
        Environment.SetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT", null);
    }
}
static void CopyApplication(string source, string destination)
{
    foreach (string relative in ApplicationPayload.Files(source).Select(file => file.Path).Append(ApplicationPayload.ManifestPath))
    {
        string file = Path.Combine(destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(Path.Combine(source, relative), file, true);
    }
}
static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
static void Assert(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Throws(Action action) { try { action(); } catch (Exception error) when (error is IOException or InvalidDataException) { return; } throw new InvalidDataException("Expected rejection"); }
static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException) { return; } throw new InvalidDataException("Expected rejection"); }
sealed class InjectedStop : Exception { }
sealed class OfflineFixture : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
}
sealed class OfficialFixture(string packages) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        string file = uri.AbsoluteUri switch
        {
            "https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases/tags/v0.17.4" => "release.json",
            "https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases/174/assets?per_page=100&page=1" => "assets.json",
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/metadata/updates/legacy-policy.json" => "policy.json",
            _ when uri.AbsoluteUri.StartsWith("https://github.com/FlappiBakuse/NexusPipeline/releases/download/v0.17.4/", StringComparison.Ordinal)
                => Path.GetFileName(uri.AbsolutePath),
            _ => throw new InvalidDataException("Unexpected fixture request: " + uri)
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(Path.Combine(packages, file))) });
    }
}
