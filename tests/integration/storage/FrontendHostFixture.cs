using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class FrontendHostFixture : IAsyncDisposable
{
    internal string Root { get; private set; } = "";
    internal string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal Uri Address { get; private set; } = null!;
    private bool _remoteAccess;
    private bool _interruptible;
    private ConsoleHostFixture? _console;
    private Process? _host;
    private Task<string>? _errors;
    private string _runtimeRoot = "";
    private string _executable = "";

    internal async Task StartAsync(BuiltExtensionInputs inputs, Uri? deniedProxy = null,
        IReadOnlyList<BuiltExtensionInputs>? additional = null, Action<ExtensionCatalogFixture>? seed = null,
        string? application = null, bool bundleInstall = false, bool defaultBundle = false, bool remoteAccess = false,
        Func<ExtensionCatalogFixture, BoundPackageFixture>? controlledPackage = null, bool interruptible = false,
        Action<string>? changeInitial = null)
    {
        if (remoteAccess && Environment.GetEnvironmentVariable("NEXUS_TEST_REMOTE_ACCESS") != "1")
            throw new IOException("NEXUS_TEST_REMOTE_ACCESS_required_for_lan_test");
        _remoteAccess = remoteAccess; _interruptible = interruptible;
        RuntimeInstance instance;
        string executable;
        using (var catalog = new ExtensionCatalogFixture(useHostTestTrust: true))
        {
            instance = catalog.Instance; Root = Path.GetDirectoryName(instance.RuntimeRoot)!;
            var software = Path.Combine(Root, "host");
            if (application is null) MaintenanceBinaryFixture.CopyBuild(software);
            else CopyApplication(application, software);
            executable = Path.Combine(software, "NexusPipeline.exe");
            using var packages = new FrontendPackages(catalog, new[] { inputs }.Concat(additional ?? []).ToArray(), controlledPackage?.Invoke(catalog));
            packages.Publish();
            seed?.Invoke(catalog);
            var settings = new HostSettingsStore(catalog.Database); var before = settings.Read();
            var change = new HostSettingsChange(before.Revision, before.Values with
                { Service = before.Values.Service with { AllowRemoteAccess = remoteAccess },
                    Updates = before.Values.Updates with { CheckEnabled = false },
                    Proxy = deniedProxy is null ? before.Values.Proxy : new(ProxyMode.Http, deniedProxy.AbsoluteUri, "") });
            _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
            var secrets = new SecretFieldService(instance, catalog.Database, catalog.Lease, catalog.Resources, catalog.Index, new());
            using var token = new SecretValue(Token);
            await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostControlBearer(instance.InstanceId), 0, SecretDisposition.Set, token, default);
            if (bundleInstall || defaultBundle)
            {
                var bundle = Path.Combine(Root, "installation-bundle");
                packages.WriteBundle(bundle);
                if (defaultBundle)
                {
                    var initial = Path.Combine(software, "package-info", "initial-extensions");
                    packages.WriteDefaults(initial);
                    changeInitial?.Invoke(initial);
                }
                catalog.Lease.Dispose();
                if (bundleInstall) await InstallBundleAsync(executable, instance.RuntimeRoot, bundle);
            }
            else await packages.InstallAsync(Root, executable);
        }
        _runtimeRoot = instance.RuntimeRoot;
        _executable = executable;
        await StartExistingAsync(executable, _runtimeRoot);
    }

    internal async Task ColdRestartAsync(bool preservePort = false)
    {
        var port = preservePort ? Address.Port : 0;
        await DisposeAsync();
        _host = null; _errors = null;
        await StartExistingAsync(_executable, _runtimeRoot, port);
    }

    internal async Task ReinstallBundleAsync()
    {
        await DisposeAsync(); _host = null; _errors = null;
        await InstallBundleAsync(_executable, _runtimeRoot, Path.Combine(Root, "installation-bundle"));
        await StartExistingAsync(_executable, _runtimeRoot);
    }

    internal async Task ChangeStoppedAsync(Action<string> change)
    {
        await DisposeAsync(); _host = null; _errors = null;
        change(_runtimeRoot);
        await StartExistingAsync(_executable, _runtimeRoot);
    }

    private async Task StartExistingAsync(string executable, string runtimeRoot, int port = 0)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = !_interruptible, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "serve", "--runtime-root", runtimeRoot, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        StreamReader output;
        if (_interruptible)
        {
            _console = new(executable, runtimeRoot, port);
            _host = _console.Process; _errors = _console.Errors; output = _console.Output;
        }
        else
        {
            _host = Process.Start(start) ?? throw new IOException("frontend_host_start_failed");
            _errors = _host.StandardError.ReadToEndAsync(); output = _host.StandardOutput;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var line = await output.ReadLineAsync(deadline.Token);
        if (string.IsNullOrEmpty(line)) throw new IOException("frontend_host_ready_missing");
        File.WriteAllText(Path.Combine(Root, "frontend-host-ready.json"), line);
        using var ready = JsonDocument.Parse(line);
        var local = new Uri(ready.RootElement.GetProperty("address").GetString()!);
        if (!_remoteAccess) { Address = local; return; }
        var network = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => item.Address)
            .First(item => item.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(item));
        Address = new($"http://{network}:{local.Port}");
    }

    internal async Task InterruptAsync()
    {
        if (!_interruptible || _host is null || _host.HasExited) throw new IOException("frontend_host_not_interruptible");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var value in new[] { "console-stop-fixture", _host.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _executable, _host.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(value);
        using var signal = Process.Start(start) ?? throw new IOException("fixture_signal_not_started");
        await signal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
        if (signal.ExitCode != 0) throw new IOException("fixture_signal_failed:" + signal.ExitCode);
        await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (_host.ExitCode != 0) throw new IOException("fixture_host_interruption_failed");
    }

    internal async Task KillOwnerAsync()
    {
        if (_host is null || _host.HasExited) throw new IOException("frontend_host_not_running");
        _host.Kill(); await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void CopyApplication(string application, string destination)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(application, "assembly.json")));
        foreach (var item in receipt.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString()!; PhysicalPathPolicy.ValidateRelativePath(relative);
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(application, relative));
            using (var original = File.OpenRead(path))
                if (original.Length != item.GetProperty("sizeBytes").GetInt64() ||
                    Convert.ToHexStringLower(SHA256.HashData(original)) != item.GetProperty("sha256").GetString())
                    throw new IOException("product_application_copy_changed");
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: false);
            using var source = File.OpenRead(path);
            using var copied = File.OpenRead(target);
            if (!SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(copied)))
                throw new IOException("product_application_copy_changed");
        }
    }

    private async Task InstallBundleAsync(string executable, string runtimeRoot, string bundle)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "install-bundle", "--runtime-root", runtimeRoot, "--bundle", bundle }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("bundle_installer_start_failed");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
            File.WriteAllText(Path.Combine(Root, "bundle-installer.stdout.log"), await output);
            File.WriteAllText(Path.Combine(Root, "bundle-installer.stderr.log"), await errors);
            if (process.ExitCode != 0) throw new IOException("bundle_installer_failed");
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        try
        {
            if (!_host.HasExited) _host.Kill();
            await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (_errors is not null) File.WriteAllText(Path.Combine(Root, "frontend-host.stderr.log"), await _errors);
        }
        finally { _host.Dispose(); _console?.Dispose(); _console = null; }
    }
}
