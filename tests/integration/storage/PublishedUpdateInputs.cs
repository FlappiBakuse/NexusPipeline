using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Maintenance;
using UpdateRecipe = NexusPipeline.Platform.Storage.Maintenance.UpdateRecipe;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class PublishedUpdateInputs : IDisposable
{
    private readonly string _receiptPath;
    private readonly byte[] _receiptBytes;
    private readonly JsonDocument _assembly;
    private readonly string _application;
    private readonly string _archive;
    private readonly string _archiveHash;
    private readonly string _setup;

    private readonly Dictionary<string, (string Source, byte[] Bytes)> _derived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _manifests = new(StringComparer.OrdinalIgnoreCase);

    internal PublishedUpdateInputs(bool deriveRuntime = false)
    {
        _receiptPath = Environment.GetEnvironmentVariable("NEXUS_PRODUCT_ASSEMBLY") ?? throw new IOException("product_assembly_required");
        _receiptBytes = File.ReadAllBytes(_receiptPath);
        _assembly = JsonDocument.Parse(_receiptBytes);
        Assert.Equal("PASS_APPLICATION_ASSEMBLED", _assembly.RootElement.GetProperty("status").GetString());
        Assert.Equal("test", _assembly.RootElement.GetProperty("profile").GetString());
        _application = Path.GetDirectoryName(_receiptPath)!;
        var assetsPath = Environment.GetEnvironmentVariable("NEXUS_PRODUCT_ASSETS") ?? throw new IOException("product_assets_required");
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        Assert.Equal("PASS_THREE_ASSETS_BUILT_NOT_QUALIFIED", assets.RootElement.GetProperty("status").GetString());
        Assert.Equal(Digest(_receiptBytes), assets.RootElement.GetProperty("assembly").GetProperty("sha256").GetString());
        var item = assets.RootElement.GetProperty("assets").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString()!.EndsWith("-Update.zip", StringComparison.Ordinal));
        _archive = Path.Combine(Path.GetDirectoryName(assetsPath)!, item.GetProperty("name").GetString()!);
        _archiveHash = item.GetProperty("sha256").GetString()!;
        var setup = assets.RootElement.GetProperty("assets").EnumerateArray().Single(value => value.GetProperty("name").GetString()!.EndsWith("-Setup.exe", StringComparison.Ordinal));
        _setup = Path.Combine(Path.GetDirectoryName(assetsPath)!, setup.GetProperty("name").GetString()!);
        Assert.Equal(setup.GetProperty("sha256").GetString(), Digest(File.ReadAllBytes(_setup)));
        Assert.Equal(item.GetProperty("sizeBytes").GetInt64(), new FileInfo(_archive).Length);
        if (deriveRuntime)
        {
            var bundle = Environment.GetEnvironmentVariable("NEXUS_TEST_BUNDLE_ROOT") ?? throw new IOException("test_bundle_required");
            foreach (var file in _assembly.RootElement.GetProperty("files").EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString()!;
                if (relative is not (ApplicationTree.HostEntry or ApplicationTree.MaintenanceEntry)) continue;
                var helper = relative == ApplicationTree.MaintenanceEntry;
                var source = Path.Combine(bundle, helper ? "maintenance" : "host",
                    helper ? "NexusPipeline.Maintenance.exe" : "NexusPipeline.Host.exe");
                _derived.Add(relative, (source, File.ReadAllBytes(source)));
            }
        }
        VerifyUnchanged();
    }

    internal void CopyOriginal(string target)
    {
        VerifyPayload(_application);
        foreach (var item in _assembly.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString()!;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(_application, relative), destination);
        }
        foreach (var (relative, input) in _derived) File.WriteAllBytes(Path.Combine(target, relative), input.Bytes);
        if (_derived.Count != 0) _manifests[target] = ApplicationPayloadSeed.Write(target);
    }

    internal void InstallOriginal(string target, string context)
    {
        var start = new System.Diagnostics.ProcessStartInfo(_setup)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOICONS", "/LANG=en",
            "/DIR=" + target, "/LOG=" + Path.Combine(context, "native-install.log") }) start.ArgumentList.Add(argument);
        start.Environment["NEXUS_TEST_ROOT"] = context;
        start.Environment["TEMP"] = start.Environment["TMP"] = context;
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("fixture_setup_start_failed");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000)) { process.Kill(entireProcessTree: true); throw new IOException("fixture_setup_deadline"); }
        File.WriteAllText(Path.Combine(context, "install.stdout.log"), stdout.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(context, "install.stderr.log"), stderr.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(context, "install.command.json"), JsonSerializer.Serialize(new { start.FileName, process.ExitCode }));
        Assert.Equal(0, process.ExitCode);
        VerifyPayload(target);
    }

    internal FrozenPayloadApply BindOwnedCandidate(RuntimeInstance instance, StateDatabase database, FrozenPayloadApply plan, string candidate,
        HostUpdateTiming timing, bool desktop)
    {
        var readme = Path.Combine(candidate, "README.md");
        File.WriteAllText(readme, "Controlled installed maintenance target");
        var path = Path.Combine(candidate, ApplicationTree.ManifestPath);
        var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        var item = manifest["files"]!.AsArray().Single(value => value!["path"]!.GetValue<string>() == "README.md")!;
        item["sizeBytes"] = new FileInfo(readme).Length; item["sha256"] = Digest(File.ReadAllBytes(readme));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(path, bytes);
        var archive = Path.Combine(Path.GetDirectoryName(candidate)!, "owned-derived-update.zip");
        ApplicationPayloadSeed.Archive(candidate, archive, bytes);
        return UpdateSourceSeed.BindArchive(instance, database, plan, candidate, archive, bytes, UpdateSourceSeed.StoreBinding(),
            "0.18.0", "test", timing, new(desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Headless, desktop, desktop ? "/settings" : "/"));
    }

    internal void ExtractCandidate(string target)
    {
        using var zip = ZipFile.OpenRead(_archive);
        var expected = _assembly.RootElement.GetProperty("files").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!).Where(name => !name.StartsWith("plugins/", StringComparison.Ordinal))
            .Concat(UpdateRecipe.Metadata).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
        zip.ExtractToDirectory(target);
        VerifyPayload(target, derived: false, bundled: false);
        if (_derived.Count != 0)
        {
            foreach (var (relative, input) in _derived)
                File.WriteAllBytes(Path.Combine(target, relative), input.Bytes);
            _manifests[target] = ApplicationPayloadSeed.Write(target);
        }
    }

    internal FrozenPayloadApply Bind(RuntimeInstance instance, StateDatabase database, FrozenPayloadApply plan, string candidate,
        HostUpdateTiming timing, bool desktop)
    {
        Assert.Empty(_derived);
        var bytes = File.ReadAllBytes(Path.Combine(candidate, ApplicationTree.ManifestPath));
        using var manifest = JsonDocument.Parse(bytes);
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        return UpdateSourceSeed.BindArchive(instance, database, plan, candidate, _archive, bytes,
            UpdateSourceSeed.StoreBinding(), "0.18.0", "test", timing,
            new(desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Headless, desktop, desktop ? "/settings" : "/"));
    }

    internal void VerifyPayload(string root, bool derived = true, bool bundled = true)
    {
        var files = _assembly.RootElement.GetProperty("files").EnumerateArray()
            .Where(item => bundled || !item.GetProperty("path").GetString()!.StartsWith("plugins/", StringComparison.Ordinal)).ToArray();
        var expectedNames = files.Select(item => item.GetProperty("path").GetString()!)
            .Where(name => !name.StartsWith("plugins/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        Assert.Equal(expectedNames, ApplicationTree.Capture(new(SnapshotBudget.Default), root, default).Entries
            .Where(entry => entry.Kind == FileObjectKind.File).Select(entry => entry.RelativePath).Order(StringComparer.Ordinal));
        foreach (var item in files)
        {
            var relative = item.GetProperty("path").GetString()!;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
                Assert.False((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            using var stream = File.OpenRead(path);
            _derived.TryGetValue(relative, out var input);
            var overridden = derived && root != _application && input.Bytes is not null;
            if (derived && relative == ApplicationTree.ManifestPath && _manifests.TryGetValue(root, out var bytes))
            { Assert.Equal(bytes, File.ReadAllBytes(path)); continue; }
            Assert.Equal(overridden ? input.Bytes!.LongLength : item.GetProperty("sizeBytes").GetInt64(), stream.Length);
            Assert.Equal(overridden ? Digest(input.Bytes!) : item.GetProperty("sha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    internal void VerifyUnchanged()
    {
        foreach (var input in _derived.Values) Assert.Equal(input.Bytes, File.ReadAllBytes(input.Source));
        Assert.Equal(_receiptBytes, File.ReadAllBytes(_receiptPath));
        using var stream = File.OpenRead(_archive);
        Assert.Equal(_archiveHash, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() => _assembly.Dispose();
}
