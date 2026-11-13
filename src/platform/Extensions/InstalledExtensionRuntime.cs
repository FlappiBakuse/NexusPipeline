using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledExtensionRuntime : IAsyncDisposable
{
    private readonly PreparedExtensionActivation _source;
    private readonly ExtensionScope _scope;
    private readonly IDisposable _files;
    private readonly InstalledNativeSlot _nativeSlot;
    private readonly InstalledNativeSource _nativeSource;
    private readonly InstalledNativeLifetime? _nativeLifetime;
    private readonly PackageLoadContext? _context;
    private readonly ConstructorInfo? _entry;
    private readonly ExtensionActivationContext? _activationContext;
    private readonly TaskAdapterExecutor? _adapter;
    private readonly InstalledExtensionConsumption? _adapterConsumption;
    private readonly ExecutionProviderRegistry? _providers;
    private readonly InstalledExtensionConsumption? _consumption;
    internal InstalledFrontend? Frontend { get; private set; }
    internal Guid ActivationId => _scope.ActivationId;
    private int _disposed;

    internal IDisposable ProtectWorkTarget(string workspace)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = Storage.PhysicalPathPolicy.NormalizeWithoutLinks(_source.TargetPath);
        workspace = Storage.PhysicalPathPolicy.NormalizeWithoutLinks(workspace);
        if (Overlaps(current, workspace) || Overlaps(workspace, current))
            throw new IOException("extension_work_scope_referenced");
        // A scope use keeps its package and native lifetime held while another installation's tree retires.
        return _scope.BeginUse();
    }

    private static bool Overlaps(string parent, string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(parent, path) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private InstalledExtensionRuntime(PreparedExtensionActivation source, ExtensionScope scope, IDisposable files, InstalledNativeSlot nativeSlot, InstalledNativeSource nativeSource,
        PackageLoadContext? context, ConstructorInfo? entry, ExtensionActivationContext? activationContext,
        TaskAdapterExecutor? adapter, ExecutionProviderRegistry? providers, InstalledExtensionConsumption? adapterConsumption = null)
    {
        _source = source; _scope = scope; _files = files; _nativeSlot = nativeSlot; _nativeSource = nativeSource;
        _context = context; _entry = entry; _activationContext = activationContext; _adapter = adapter; _providers = providers; _adapterConsumption = adapterConsumption;
        _consumption = adapterConsumption;
        if (context is { HasDeclaredNative: true }) _nativeLifetime = new(files, context.Unload);
    }

    internal static InstalledExtensionRuntime Load(PreparedExtensionActivation source, ExtensionScope scope, ContractReader contracts, ExecutionProviderRegistry? providers,
        InstalledPortFactory? ports, InstalledNativeSlot nativeSlot)
    {
        using var timing = Storage.StartupProfile.Measure("managed.load");
        source.RequireBeforeCallback();
        var manifest = source.ReadManifest();
        var files = source.HoldRuntimeFiles();
        PackageLoadContext? context = null;
        try
        {
            var nativeSource = source.CaptureNativeSource(); nativeSlot.RequireSource(nativeSource);
            if (manifest.Kind == "managed-code")
            {
                var entryPath = source.ResolveFile(manifest.Managed!.EntryAssembly);
                context = new(source.AssemblyFiles(), entryPath,
                    (manifest.Managed.NativeLibraries ?? []).Select(source.ResolveFile).ToImmutableArray(), scope, nativeSlot);
                var assembly = context.LoadEntry(entryPath);
                var entry = assembly.GetType(manifest.Managed.EntryType, throwOnError: true, ignoreCase: false)!;
                if (!entry.IsPublic || entry.IsAbstract || !typeof(IExtension).IsAssignableFrom(entry) ||
                    entry.GetConstructor([typeof(ExtensionActivationContext)]) is null) throw new IOException("extension_managed_entry_invalid");
                var activationContext = (ports ?? throw new IOException("extension_port_factory_unavailable")).Create(source, scope, files, out var consumption);
                return new(source, scope, files, nativeSlot, nativeSource, context, entry.GetConstructor([typeof(ExtensionActivationContext)]), activationContext, manifest.Specialized is null ? null : ReadAdapter(source, scope, contracts), providers,
                    consumption);
            }
            if (manifest.Kind != "data-specialized") throw new IOException("extension_kind_unavailable");
            var adapterPath = source.ResolveFile(manifest.Specialized!.AdapterEntry);
            var bytes = File.ReadAllBytes(adapterPath);
            if (bytes.Length is < 1 or > 262144) throw new IOException("extension_adapter_size_invalid");
            var script = new UTF8Encoding(false, true).GetString(bytes);
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            var adapter = new TaskAdapterExecutor(source.ReadManifestBytes(), scope, contracts,
                manifest.Specialized.AdapterEntry, script, sha);
            return new(source, scope, files, nativeSlot, nativeSource, null, null, null, adapter, providers,
                (ports ?? throw new IOException("extension_port_factory_unavailable")).CreateConsumption(source, scope));
        }
        catch { try { context?.ReleaseNativeLibraries(); } finally { context?.Unload(); files.Dispose(); } throw; }
    }

    private static TaskAdapterExecutor ReadAdapter(PreparedExtensionActivation source, ExtensionScope scope, ContractReader contracts)
    {
        var manifest = source.ReadManifest();
        var path = manifest.Specialized!.AdapterEntry;
        var bytes = File.ReadAllBytes(source.ResolveFile(path));
        if (bytes.Length is < 1 or > 262144) throw new IOException("extension_adapter_size_invalid");
        return new(source.ReadManifestBytes(), scope, contracts, path, new UTF8Encoding(false, true).GetString(bytes),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    internal async ValueTask ActivateAsync(ExtensionScope scope, CancellationToken cancellation, VerifiedProviderPackage.Prepared? preparedProvider)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _source.RequireBeforeCallback(); cancellation.ThrowIfCancellationRequested();
        if (_entry is not null)
        {
            var extension = (IExtension)_entry.Invoke([_activationContext!]);
            if (extension is IAsyncDisposable lifetime) await scope.OwnAsync(lifetime).ConfigureAwait(false);
            using (Storage.StartupProfile.Measure("managed.callback"))
                await extension.ActivateAsync(scope, cancellation).ConfigureAwait(false);
            if (_source.ReadManifest().Frontend is not null)
                Frontend = new(_source.ReadManifest(), _activationContext!.PackageAssets,
                    _consumption!, scope, extension as IExtensionFrontend, _activationContext.Activation!);
        }
        cancellation.ThrowIfCancellationRequested(); scope.Cancellation.ThrowIfCancellationRequested();
        if (_source.ReadManifest().Execution is not null)
        {
            var registry = _providers ?? throw new IOException("extension_execution_registration_unavailable");
            var prepared = preparedProvider ?? throw new IOException("extension_provider_preparation_missing");
            await new ExecutionRegistrationPort(scope, prepared.Package, registry, prepared).RegisterAsync(cancellation).ConfigureAwait(false);
        }
    }

    internal ValueTask<T> InvokeAdapterAsync<T>(NexusPipeline.Core.Maintenance.ResourceOperationLease actualRoot,
        Action requireInput, Func<TaskAdapterExecutor, Action, ValueTask<T>> callback)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var adapter = RequireAdapter();
        var consumption = _adapterConsumption ?? throw new IOException("extension_adapter_consumption_unavailable");
        return consumption.InvokeAcceptedAsync(actualRoot, requireInput, async () =>
        {
            using var use = consumption.Begin();
            var result = await callback(adapter, use.RequireCurrent).ConfigureAwait(false);
            use.RequireCurrent(); return result;
        });
    }

    internal TaskAdapterExecutor RequireAdapter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _adapter ?? throw new IOException("extension_adapter_unavailable");
    }

    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return ValueTask.CompletedTask;
        RuntimeCleanupGuard.RequireRelease(_scope);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _context?.ReleaseNativeLibraries(); }
            catch
            {
                RetainOrRelease(); throw;
            }
            RetainOrRelease();
        }
        return ValueTask.CompletedTask;
    }

    private void RetainOrRelease()
    {
        var context = _context;
        if (context?.HasUnwitnessedNative() == true) throw new IOException("extension_native_resident_restart_required");
        var remaining = context?.ResidualNative() ?? ImmutableDictionary<string, InstalledNativeMapping>.Empty;
        if (!remaining.IsEmpty)
        {
            _nativeSlot.Retain(_nativeSource, remaining, _files, context!.Unload);
            _nativeLifetime?.Transfer();
        }
        else if (_nativeLifetime is not null) _nativeLifetime.Release();
        else { try { context?.Unload(); } finally { _files.Dispose(); } }
    }

    private sealed class PackageLoadContext : AssemblyLoadContext
    {
        private readonly ImmutableDictionary<string, (string Path, string Identity)> _assemblies;
        private readonly InstalledNativeLibraries _native;
        internal bool HasDeclaredNative { get; }
        private static readonly Assembly Sdk = typeof(IExtension).Assembly;
        private static readonly ImmutableHashSet<string> HostNames = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            typeof(InstalledExtensionRuntime).Assembly.GetName().Name!, typeof(NexusPipeline.Core.Extensions.ExtensionApplyPolicy).Assembly.GetName().Name!,
            "NexusPipeline.Host");
        private static readonly string RuntimeRoot = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        private static readonly ImmutableHashSet<string> RuntimeNames = Directory.EnumerateFiles(RuntimeRoot, "*.dll")
            .Select(path => Path.GetFileNameWithoutExtension(path)).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        internal PackageLoadContext(ImmutableArray<string> files, string entryPath, ImmutableArray<string> nativeFiles,
            ExtensionScope scope, InstalledNativeSlot nativeSlot) : base(isCollectible: true)
        {
            using var timing = Storage.StartupProfile.Measure("managed.metadata");
            var candidates = new List<(string Path, AssemblyName Name)>();
            foreach (var path in files)
            {
                try { candidates.Add((path, AssemblyName.GetAssemblyName(path))); }
                catch (BadImageFormatException) { }
            }
            var map = ImmutableDictionary.CreateBuilder<string, (string Path, string Identity)>(StringComparer.OrdinalIgnoreCase);
            Add(entryPath);
            _assemblies = map.ToImmutable();
            _native = new(nativeFiles, scope, nativeSlot); HasDeclaredNative = !nativeFiles.IsEmpty;

            void Add(string path)
            {
                var name = candidates.Single(item => item.Path == path).Name;
                if (name.Name is null || name.FullName is null || RuntimeNames.Contains(name.Name) ||
                    StringComparer.OrdinalIgnoreCase.Equals(name.Name, Sdk.GetName().Name) || HostNames.Contains(name.Name))
                    throw new IOException("extension_managed_assembly_conflict");
                if (map.TryGetValue(name.Name, out var existing))
                {
                    if (existing.Path != path || existing.Identity != name.FullName)
                        throw new IOException("extension_managed_assembly_conflict");
                    return;
                }
                map.Add(name.Name, (path, name.FullName));
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var pe = new PEReader(input);
                var metadata = pe.GetMetadataReader();
                foreach (var handle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                    if (HostNames.Contains(reference)) throw new IOException("extension_managed_dependency_not_declared");
                    if (StringComparer.OrdinalIgnoreCase.Equals(reference, Sdk.GetName().Name) || RuntimeNames.Contains(reference)) continue;
                    var choices = candidates.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name.Name, reference)).ToArray();
                    var siblings = choices.Where(item => Path.GetDirectoryName(item.Path) == Path.GetDirectoryName(path)).ToArray();
                    var selected = siblings.Length != 0 ? siblings : choices;
                    if (selected.Length != 1) throw new IOException("extension_managed_dependency_not_declared");
                    Add(selected[0].Path);
                }
            }
        }

        internal Assembly LoadEntry(string path)
        {
            if (!_assemblies.Values.Any(item => item.Path == path)) throw new IOException("extension_managed_entry_unbound");
            return LoadFromAssemblyPath(path);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(name.Name, Sdk.GetName().Name))
            {
                if (name.FullName != Sdk.FullName) throw new FileLoadException("extension_sdk_assembly_mismatch");
                return Sdk;
            }
            if (name.Name is not null && RuntimeNames.Contains(name.Name))
            {
                var assembly = Default.LoadFromAssemblyName(name);
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(assembly.Location), RuntimeRoot))
                    throw new FileLoadException("extension_runtime_assembly_unbound");
                return assembly;
            }
            if (name.Name is null || !_assemblies.TryGetValue(name.Name, out var file) || file.Identity != name.FullName)
                throw new FileLoadException("extension_dependency_not_declared");
            return LoadFromAssemblyPath(file.Path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) => _native.Load(unmanagedDllName);

        internal void ReleaseNativeLibraries() => _native.Dispose();
        internal ImmutableDictionary<string, InstalledNativeMapping> ResidualNative() => _native.Residual();
        internal bool HasUnwitnessedNative() => _native.HasUnwitnessedNative();
    }
}
