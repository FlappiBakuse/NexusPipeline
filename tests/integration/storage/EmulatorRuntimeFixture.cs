using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Emulation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class EmulatorRuntimeFixture : IDisposable
{
    internal AndroidRuntimeFixture Runtime { get; }
    internal InstallRuntimeFixture Installed { get; }
    internal SpecializedPackageInputs Package { get; }
    internal EmulatorTargetService Emulators { get; }
    internal ExecutionAdmissionService Admission { get; }
    internal string Vendor { get; }
    internal string VmDirectory { get; }
    internal string Adb { get; }
    internal EmulatorTargetOption Selected { get; }
    internal AttemptAcceptanceResult? Accepted { get; private set; }
    internal AttemptWriterState State => AttemptWriterState.Read(Runtime.Source.Instance, Runtime.Source.Database, new(), Accepted!.JournalId);
    private readonly ExtensionCatalogFixture _catalog;
    private readonly Guid _rootId;

    internal EmulatorRuntimeFixture(string vendor, bool close = true, bool mainWait = false)
    {
        Vendor = vendor; Runtime = new(forceClose: close, accept: false, capture: true, mainWait: mainWait);
        _catalog = Runtime.Base.CreateCatalog();
        Package = new("emulator-support", "NEXUS_FORMAL_EMULATOR_PACKAGE_RECEIPT", "managed-code");
        Installed = new(originalManifest: Package.Manifest, originalManifestBytes: Package.ManifestBytes, packageArchive: Package.Archive, catalog: _catalog);
        _ = Installed.Session().ApplyAsync(Installed.Prepared, default).GetAwaiter().GetResult();
        Installed.Prepared.Dispose(); Installed.Verified.Dispose(); Installed.Root.Dispose();
        var source = Runtime.Source;
        _rootId = source.Definitions.Read().Roots.Single(root => root.Path == Runtime.Directory).Id;
        var control = vendor switch { "ldplayer" => "ldconsole", "nox" => "NoxConsole", _ => "HD-Player" };
        var adb = vendor switch { "ldplayer" => "adb", "nox" => "nox_adb", _ => "HD-Adb" };
        Alias(Runtime.ToolImage, Path.Combine(Runtime.Directory, control + ".exe"));
        Adb = Path.Combine(Runtime.Directory, adb + ".exe"); Alias(Runtime.ToolImage, Adb);
        VmDirectory = Path.Combine(Runtime.Directory, vendor switch { "ldplayer" => "vms/leidian0", "nox" => "BignoxVMS/nox", _ => "Engine/Pie64" });
        Directory.CreateDirectory(VmDirectory);
        var config = vendor switch { "nox" => Path.Combine(VmDirectory, "nox.vbox"), "bluestacks" => Path.Combine(Runtime.Directory, "bluestacks.conf"), _ => null };
        if (config is not null) File.WriteAllText(config, vendor == "nox"
            ? "<VirtualBox><Machine><Hardware><Network><Adapter><NAT><Forwarding name=\"adb\" proto=\"1\" hostport=\"16384\" guestport=\"5555\"/></NAT></Adapter></Network></Hardware></Machine></VirtualBox>"
            : "bst.instance.Pie64.display_name=\"Controlled VM\"\nbst.instance.Pie64.adb_port=\"16384\"\n");
        File.WriteAllBytes(Path.Combine(Runtime.Directory, "fixture-vendor.json"), JsonSerializer.SerializeToUtf8Bytes(new { vendor, nonce = Runtime.Nonce }));
        Device(node => { node["online"] = false; if (vendor == "ldplayer") { node["endpoint"] = "127.0.0.1:5555"; node["port"] = 5555; } });
        var shared = Path.Combine(source.Root, "shared-emulator-data"); Directory.CreateDirectory(shared);
        Emulators = new(source.Instance, source.Database, new(), source.Lease, Runtime.Base.Resources, Installed.Roots, Installed.Activator, () => [], shared);
        var catalog = source.Definitions.Read();
        var found = Emulators.DiscoverAsync(source.ScriptId, catalog.Revision, [_rootId], () => { }, default).AsTask().GetAwaiter().GetResult();
        Selected = Assert.Single(found.Targets, target => vendor != "ldplayer" || target.Endpoint == "127.0.0.1:5555"); Assert.Equal(vendor, Selected.Selection.Vendor);
        var script = catalog.Scripts.Single(script => script.Id == source.ScriptId);
        var android = script.Target!.Android! with { Tool = new(_rootId, adb + ".exe"), Endpoint = Selected.Endpoint,
            Emulator = new(vendor, _rootId, Selected.Selection.TargetId, Selected.Selection.Generation) };
        var changes = new AutomationChanges(catalog.Revision, [], [script with { Target = script.Target with { Android = android } }], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "emulator-fixture", changes), changes, DateTimeOffset.UtcNow);
        Admission = new(source.Instance, source.Database, new(), source.Lease, Runtime.Base.Resources, Runtime.Base.Index,
            Runtime.Base.Inbox, new(60, 6, 7), payloadRoots: Installed.Roots,
            judgementRuntime: new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null));
        Admission.ConfigureEmulators(Emulators);
    }

    internal void Device(Action<JsonObject> change)
    {
        var path = Path.Combine(Runtime.Directory, "fixture-device.json"); var node = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); change(node);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(node));
    }

    internal AttemptAcceptanceResult Start() => Accepted = Admission.Start(Guid.NewGuid(), "emulator-fixture",
        Runtime.Source.Request(new BindingTarget(Runtime.Source.BindingA)), DateTimeOffset.UtcNow);

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Runtime.Source.Instance, Runtime.Source.Database, new(), Runtime.Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), "emulator-fixture", Accepted!.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    private static void Alias(string original, string target)
    {
        var before = Path.GetFileNameWithoutExtension(original); var after = Path.GetFileNameWithoutExtension(target);
        var bytes = File.ReadAllBytes(original); var marker = Encoding.UTF8.GetBytes(before + ".dll\0");
        var offset = bytes.AsSpan().IndexOf(marker); var replacement = Encoding.UTF8.GetBytes(after + ".dll\0");
        if (offset < 0 || replacement.Length > marker.Length) throw new IOException("fixture_apphost_alias_invalid");
        bytes.AsSpan(offset, marker.Length).Clear(); replacement.CopyTo(bytes.AsSpan(offset)); File.WriteAllBytes(target, bytes);
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            File.Copy(Path.ChangeExtension(original, suffix), Path.ChangeExtension(target, suffix));
    }

    public void Dispose()
    {
        File.WriteAllText(Path.Combine(Runtime.Directory, "fixture-vm-stop"), Runtime.Nonce);
        var marker = Path.Combine(Runtime.Directory, "fixture-vm.json");
        if (File.Exists(marker))
        {
            using var saved = JsonDocument.Parse(File.ReadAllBytes(marker));
            var pid = saved.RootElement.GetProperty("processId").GetInt32();
            try { using var process = Process.GetProcessById(pid); if (process.StartTime.ToUniversalTime() == saved.RootElement.GetProperty("createdAt").GetDateTime()) process.WaitForExit(10000); }
            catch (ArgumentException) { }
        }
        Installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Installed.Dispose(); Package.VerifyUnchanged(); Package.Dispose(); _catalog.Dispose(); Runtime.Dispose();
    }
}
