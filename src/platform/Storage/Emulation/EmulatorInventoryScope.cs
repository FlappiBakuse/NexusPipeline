using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Emulation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Emulation;

internal sealed record EmulatorCommandEvidence(string ActionId, string InstallationId, ExecutionPlanInterpreterImage Image,
    string[] Arguments, int? ExitCode, bool Natural, OwnedTargetInventory Stopped, byte[] StandardOutput, byte[] StandardError);

[SupportedOSPlatform("windows")]
internal sealed class EmulatorInventoryScope : IEmulatorInventory, IAsyncDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly ResourceOperationLease _resource;
    private readonly Action _authorize;
    private readonly string[] _roots;
    private readonly EmulatorSourceLease _source = new();
    private readonly Dictionary<string, (EmulatorInstallation Value, ExecutionPlanInterpreterImage Control)> _installations = [];
    private readonly List<EmulatorCommandEvidence> _evidence = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _timeout;
    private int _closing, _commands;
    private Task? _disposal;

    private EmulatorInventoryScope(RuntimeInstance instance, InstanceLease lease, ResourceOperationLease resource,
        string[] roots, Action authorize, TimeSpan timeout)
    {
        _instance = instance; _lease = lease; _resource = resource; _roots = roots; _authorize = authorize; _timeout = timeout;
        try { foreach (var root in roots) _source.HoldRoot(root); }
        catch { _source.Dispose(); resource.Dispose(); throw; }
    }

    internal static EmulatorInventoryScope Prepare(RuntimeInstance instance, InstanceLease lease, ResourceLeaseManager resources,
        IReadOnlyList<string> selectedRoots, Action authorize, TimeSpan timeout)
    {
        lease.VerifyHeld(instance); authorize();
        if (selectedRoots.Count is < 1 or > 32 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("emulator_inventory_scope_invalid");
        var roots = selectedRoots.Select(PhysicalPathPolicy.NormalizeWithoutLinks).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(path => !Directory.Exists(path) || Path.GetDirectoryName(path) is null)) throw new IOException("emulator_inventory_root_invalid");
        var claims = PhysicalResourceAdmission.Merge(roots.SelectMany(path => PhysicalResourceAdmission.Read(path, ResourceClaimMode.Reference, false)));
        var resource = resources.TryAcquire(Guid.NewGuid(), claims).Lease ?? throw new IOException("emulator_inventory_resources_unavailable");
        return new(instance, lease, resource, roots, authorize, timeout);
    }

    public async ValueTask<IReadOnlyList<EmulatorInstallation>> DiscoverAsync(EmulatorLayout layout, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            RequireCurrent(); RequireLayout(layout); var found = new Dictionary<string, EmulatorInstallation>(StringComparer.Ordinal);
            foreach (var root in _roots)
                foreach (var relative in layout.ControlPaths)
                {
                    lifetime.Token.ThrowIfCancellationRequested(); var path = Resolve(root, relative);
                    if (!File.Exists(path)) continue;
                    var installRoot = Path.GetDirectoryName(path)!;
                    if (Path.GetFileName(installRoot).Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                        Path.GetDirectoryName(installRoot) is { } parent && _roots.Any(root => Within(root, parent))) installRoot = parent;
                    var physical = _source.HoldRoot(installRoot);
                    var id = "install:" + EmulatorSourceLease.Hash(layout.Vendor + "\n" + physical + "\n" +
                        EmulatorSourceLease.Identity(PhysicalObjectIdentity.Read(path)));
                    if (!_installations.TryGetValue(id, out var captured))
                    {
                        if (_installations.Count >= 128) throw new EmulatorException("emulator_installation_limit");
                        var control = _source.Tool(path);
                        var adb = layout.AdbPaths.Select(item => Resolve(installRoot, item)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (adb.Length > 1) throw new EmulatorException("emulator_bundled_adb_ambiguous");
                        var configs = CaptureConfigs(layout, installRoot);
                        captured = (new(id, layout.Vendor, installRoot, physical, control.Value,
                            adb.Length == 0 ? null : _source.Tool(adb[0]).Value, configs), control.Image);
                        _installations.Add(id, captured);
                    }
                    found[id] = Copy(captured.Value);
                }
            RequireCurrent(); return found.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<EmulatorCommandResult> EnumerateAsync(EmulatorInstallation installation, EmulatorCommand command, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            RequireCurrent();
            if (!_installations.TryGetValue(installation.Id, out var captured) || !Same(installation, captured.Value) || command.Detached ||
                command.ToolId != captured.Value.Control.Id || captured.Value.Vendor == EmulatorVendor.BlueStacks ||
                !command.Arguments.SequenceEqual(captured.Value.Vendor == EmulatorVendor.LdPlayer ? ["list2"] : ["list"], StringComparer.Ordinal))
                throw new EmulatorException("emulator_enumeration_unbound");
            if (++_commands > 128) throw new EmulatorException("emulator_command_limit");
            var remaining = _timeout - _clock.Elapsed; if (remaining <= TimeSpan.Zero) throw new EmulatorException("emulator_inventory_timeout");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(remaining);
            var action = Guid.NewGuid().ToString("D");
            try
            {
                var result = await OwnedCaptureCommand.RunAsync(captured.Control, command.Arguments.ToArray(), _instance.InstanceId,
                    _resource.OperationId, 1024 * 1024, 2 * 1024 * 1024, 5000, (_, _) => RequireCurrent(), deadline.Token).ConfigureAwait(false);
                RequireCurrent(); _evidence.Add(new(action, installation.Id, captured.Control, command.Arguments.ToArray(), result.ExitCode,
                    result.Natural, result.Stopped, result.StandardOutput, result.StandardError));
                return new(result.Natural ? "completed" : "incomplete", result.ExitCode, Decode(result.StandardOutput), Decode(result.StandardError), action);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return new("timeout", null, "", "", action); }
        }
        finally { _gate.Release(); }
    }

    internal IReadOnlyList<EmulatorCommandEvidence> ReadEvidence()
    {
        if (!_gate.Wait(0)) throw new IOException("emulator_inventory_operation_pending");
        try { RequireCurrent(); return _evidence.ToArray(); }
        finally { _gate.Release(); }
    }

    internal string InstanceDirectory(EmulatorTarget target)
    {
        RequireCurrent();
        if (!_installations.TryGetValue(target.Installation.Id, out var captured) || !Same(target.Installation, captured.Value))
            throw new EmulatorException("emulator_installation_unbound");
        var candidates = new List<string>();
        if (target.Installation.Vendor == EmulatorVendor.LdPlayer && target.Instance.Index is { } index)
            candidates.Add(Path.Combine(target.Installation.RootPath, "vms", "leidian" + index));
        foreach (var config in target.Installation.Configurations.Where(value => target.Instance.ConfigIds.Contains(value.Id, StringComparer.Ordinal)))
        {
            var directory = Path.GetDirectoryName(_source.ConfigPath(config))!;
            if (target.Installation.Vendor == EmulatorVendor.Nox && Path.GetFileName(directory).Equals(target.Instance.Identity, StringComparison.OrdinalIgnoreCase))
                candidates.Add(directory);
            else if (target.Installation.Vendor == EmulatorVendor.BlueStacks)
                candidates.Add(Path.Combine(directory, "Engine", target.Instance.Identity));
        }
        var found = candidates.Where(Directory.Exists).Select(PhysicalPathPolicy.NormalizeWithoutLinks)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (found.Length != 1) throw new EmulatorException("emulator_instance_directory_unavailable");
        return found[0];
    }

    private EmulatorConfig[] CaptureConfigs(EmulatorLayout layout, string installRoot)
    {
        var paths = layout.ConfigPaths.Select(relative => (Path: Resolve(installRoot, relative), Relative: relative)).ToList();
        for (var index = 0; index < _roots.Length; index++)
            foreach (var relative in layout.SharedDataPaths) paths.Add((Resolve(_roots[index], relative), "external/" + index + "/" + relative));
        var configs = new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, relative) in paths)
        {
            if (File.Exists(path)) configs.TryAdd(path, _source.Config(path, relative));
            else if (Directory.Exists(path))
                foreach (var file in _source.ConfigFiles(path)) configs.TryAdd(file,
                    _source.Config(file, relative.TrimEnd('/') + "/" + Path.GetRelativePath(path, file).Replace('\\', '/')));
        }
        return configs.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string Resolve(string root, string relative)
    {
        if (relative.Length is < 1 or > 512 || relative.Any(value => char.IsControl(value) || value is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part[^1] is ' ' or '.')) throw new EmulatorException("emulator_inventory_path_invalid");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new EmulatorException("emulator_inventory_path_invalid");
        return File.Exists(path) || Directory.Exists(path) ? PhysicalPathPolicy.NormalizeWithoutLinks(path) : path;
    }
    private static bool Within(string root, string path) => root.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RequireLayout(EmulatorLayout layout)
    {
        if (!Enum.IsDefined(layout.Vendor) || layout.ControlPaths.Count is < 1 or > 16 || layout.AdbPaths.Count > 16 ||
            layout.ConfigPaths.Count > 16 || layout.SharedDataPaths.Count > 16) throw new EmulatorException("emulator_layout_invalid");
    }
    private static bool Same(EmulatorInstallation value, EmulatorInstallation expected) => value.Id == expected.Id && value.Vendor == expected.Vendor &&
        value.RootPath == expected.RootPath && value.RootPhysicalId == expected.RootPhysicalId && value.Control == expected.Control && value.Adb == expected.Adb &&
        value.Configurations.Count == expected.Configurations.Count && value.Configurations.Zip(expected.Configurations).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.PhysicalId == pair.Second.PhysicalId && pair.First.RelativePath == pair.Second.RelativePath &&
            pair.First.Sha256 == pair.Second.Sha256 && pair.First.Revision == pair.Second.Revision && pair.First.Bytes.Span.SequenceEqual(pair.Second.Bytes.Span));
    private static EmulatorInstallation Copy(EmulatorInstallation value) => value with
        { Configurations = value.Configurations.Select(config => config with { Bytes = config.Bytes.ToArray() }).ToArray() };
    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
        catch (DecoderFallbackException) { throw new EmulatorException("emulator_cli_encoding_invalid"); }
    }
    private void RequireCurrent()
    {
        if (Volatile.Read(ref _closing) != 0) throw new ObjectDisposedException(nameof(EmulatorInventoryScope));
        _stopping.Token.ThrowIfCancellationRequested(); _lease.VerifyHeld(_instance); _authorize(); _source.RequireCurrent();
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _stopping.Cancel(); await _gate.WaitAsync().ConfigureAwait(false);
        try { _source.Dispose(); _resource.Dispose(); }
        finally { _gate.Release(); _stopping.Dispose(); }
    }
}
