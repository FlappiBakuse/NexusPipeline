using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class FrontendPackages : IDisposable
{
    private readonly ExtensionCatalogFixture _catalog;
    private readonly BoundPackageFixture[] _packages;

    internal FrontendPackages(ExtensionCatalogFixture catalog, IReadOnlyList<BuiltExtensionInputs> inputs, BoundPackageFixture? controlled = null)
    {
        _catalog = catalog;
        _packages = inputs.Select(input => new BoundPackageFixture(input.Archive, input.Manifest,
            input.ManifestBytes, catalog, ExtensionStoreChannel.Stable)).Concat(controlled is null ? [] : new[] { controlled }).ToArray();
    }

    internal void Publish()
    {
        var entries = _packages.Select(package => {
            var entry = JsonNode.Parse(package.Signed().Catalog)!["entries"]![0]!.DeepClone();
            entry["artifact"]!["url"] = new Uri(ExtensionCatalogFixture.Artifacts,
                entry["artifactName"]!.GetValue<string>() + "/" + entry["artifactName"]!.GetValue<string>() +
                "-" + entry["packageVersion"]!.GetValue<string>() + ".zip").AbsoluteUri;
            return entry;
        }).ToArray();
        var signed = _catalog.Signed(edit: document => document["entries"] = new JsonArray(entries));
        _ = _catalog.Store.Accept(Guid.NewGuid(), "local", _catalog.Verify(signed));
    }

    internal async Task InstallAsync(string root, string executable)
    {
        var parent = Path.Combine(_catalog.Instance.RuntimeRoot, "extensions"); Directory.CreateDirectory(parent);
        var requests = _packages.Select(package => {
            var manifest = JsonNode.Parse(package.ManifestBytes)!;
            var name = manifest["extensionId"]!.GetValue<string>();
            var input = Path.Combine(root, "installer-" + name + "-input.json");
            var barrier = Path.Combine(root, "installer-" + name + "-complete.json");
            File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Instance = _catalog.Instance, package.Archive, PayloadParent = parent, HostRoot = Path.GetDirectoryName(executable),
                package.OperationId, PublicKey = _catalog.Signer.ExportSubjectPublicKeyInfo(), Point = "complete-exit",
                AcceptedReport = Path.Combine(root, "installer-" + name + "-accepted.json"), BarrierReport = barrier,
                ExtensionId = name, Media = manifest["capabilities"]!.AsArray().Any(value => value!.GetValue<string>() == "media.capture")
            }));
            return (Input: input, Barrier: barrier, package.OperationId, Name: name);
        }).ToArray();
        _catalog.Lease.Dispose();
        foreach (var request in requests)
        {
            using var owner = new OwnedCrashProcess();
            _ = owner.Start(["extension-installation-recovery", request.Input], root, _catalog.Instance.InstanceId, request.OperationId,
                Path.Combine(root, "installer-" + request.Name + "-native.json"));
            _ = await owner.ReadReportAsync(request.Barrier, TimeSpan.FromSeconds(45));
            if (await owner.AwaitExitAsync() != 0 || !(await owner.StopAsync()).CompleteJobStopped)
                throw new IOException("frontend_installer_stop_unconfirmed");
        }
    }

    internal void WriteBundle(string directory)
    {
        Directory.CreateDirectory(directory);
        var catalog = _catalog.Store.ReadCached(ExtensionStoreChannel.Stable) ?? throw new IOException("bundle_catalog_missing");
        File.WriteAllBytes(Path.Combine(directory, "index.json"), catalog.PointerBytes());
        File.WriteAllBytes(Path.Combine(directory, "catalog.json"), catalog.CatalogBytes());
        File.WriteAllBytes(Path.Combine(directory, "catalog.sig.json"), catalog.SignatureBytes());
        foreach (var package in _packages)
        {
            using var manifest = JsonDocument.Parse(package.ManifestBytes);
            File.Copy(package.Archive, Path.Combine(directory, manifest.RootElement.GetProperty("artifactName").GetString()! + "-" +
                manifest.RootElement.GetProperty("packageVersion").GetString()! + ".zip"));
        }
    }

    internal void WriteDefaults(string directory)
    {
        var catalog = _catalog.Store.ReadCached(ExtensionStoreChannel.Stable) ?? throw new IOException("bundle_catalog_missing");
        foreach (var package in _packages)
        {
            using var manifest = JsonDocument.Parse(package.ManifestBytes);
            var root = manifest.RootElement;
            if (root.GetProperty("extensionId").GetString() is not ("emulator-support" or "live-screenshot")) continue;
            var artifact = root.GetProperty("artifactName").GetString()!;
            var target = Path.Combine(directory, artifact); Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, "index.json"), catalog.PointerBytes());
            File.WriteAllBytes(Path.Combine(target, "catalog.json"), catalog.CatalogBytes());
            File.WriteAllBytes(Path.Combine(target, "catalog.sig.json"), catalog.SignatureBytes());
            File.Copy(package.Archive, Path.Combine(target, artifact + "-" + root.GetProperty("packageVersion").GetString()! + ".zip"));
        }
    }

    public void Dispose() { foreach (var package in _packages) package.Dispose(); }
}
