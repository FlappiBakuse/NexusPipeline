using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledNativeLibraries : IDisposable
{
    private readonly object _gate = new();
    private readonly ExtensionScope _scope;
    private readonly ImmutableDictionary<string, string> _paths;
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _imports;
    private readonly Dictionary<string, string> _expectedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<nint> _modules = [];
    private readonly InstalledNativeSlot _slot;
    private readonly Dictionary<string, InstalledNativeMapping> _witnesses = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded, _disposed;
    private const DllImportSearchPath Search = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;

    internal InstalledNativeLibraries(ImmutableArray<string> declared, ExtensionScope scope, InstalledNativeSlot slot)
    {
        _scope = scope; _slot = slot;
        if (declared.Length > 32 || declared.Distinct(StringComparer.OrdinalIgnoreCase).Count() != declared.Length)
            throw Invalid();
        var paths = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var imports = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in declared)
        {
            var name = Path.GetFileName(path);
            if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path ||
                !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !paths.TryAdd(name, path)) throw Invalid();
            imports.Add(name, InstalledNativeImports.Read(path));
        }
        _paths = paths.ToImmutable(); _imports = imports.ToImmutable();
        foreach (var (name, path) in _paths) _expectedModules.Add(name, path);
        BindDependencies();
        RequireDirectories();
    }

    private void BindDependencies()
    {
        foreach (var (name, dependencies) in _imports)
        foreach (var dependency in dependencies)
        {
            if (_paths.TryGetValue(dependency, out var owned))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(owned), Path.GetDirectoryName(_paths[name])))
                    throw Invalid();
                continue;
            }
            if (dependency.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) ||
                dependency.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase)) continue;
            var system = Path.Combine(Environment.SystemDirectory, dependency);
            if (!File.Exists(system) || (File.GetAttributes(system) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            _expectedModules.TryAdd(dependency, system);
        }
    }

    internal nint Load(string requested)
    {
        using var use = _scope.IsPublished ? _scope.BeginUse() : _scope.BeginActivationUse();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scope.Cancellation.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(requested) || Path.GetFullPath(requested) != requested ||
                !_paths.Values.Contains(requested, StringComparer.OrdinalIgnoreCase)) throw Invalid();
            RequireDirectories(); RequireLoadedModules();
            if (!_loaded) LoadDependencies();
            var result = NativeLibrary.Load(PhysicalPathPolicy.NativePath(requested), typeof(InstalledNativeLibraries).Assembly, Search);
            try { _scope.Cancellation.ThrowIfCancellationRequested(); RequireLoadedModules(); return result; }
            catch { NativeLibrary.Free(result); throw; }
        }
    }

    private void LoadDependencies()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in _paths.Keys.Order(StringComparer.OrdinalIgnoreCase)) Load(name);
            RequireLoadedModules(); _loaded = true;
        }
        catch { ReleaseModules(); throw; }

        void Load(string name)
        {
            if (visited.Contains(name)) return;
            if (!visiting.Add(name)) throw Invalid();
            foreach (var dependency in _imports[name].Where(_paths.ContainsKey)) Load(dependency);
            _scope.Cancellation.ThrowIfCancellationRequested();
            var module = NativeLibrary.Load(PhysicalPathPolicy.NativePath(_paths[name]), typeof(InstalledNativeLibraries).Assembly, Search);
            _modules.Add(module); _witnesses[name] = new(_paths[name], module.ToInt64());
            visiting.Remove(name); visited.Add(name);
        }
    }

    private void RequireDirectories()
    {
        foreach (var group in _paths.Values.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            var actual = Directory.EnumerateFiles(group.Key!).Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(group)) throw new IOException("extension_native_directory_changed");
        }
    }

    private void RequireLoadedModules()
    {
        using var process = Process.GetCurrentProcess();
        foreach (var module in process.Modules.Cast<ProcessModule>())
        {
            var name = Path.GetFileName(module.FileName);
            if (!_expectedModules.TryGetValue(name, out var expected)) continue;
            var mapping = new InstalledNativeMapping(InstalledNativeMapping.NormalizePath(module.FileName), module.BaseAddress.ToInt64());
            if (!StringComparer.OrdinalIgnoreCase.Equals(mapping.Path, expected) || _paths.ContainsKey(name) &&
                !(_witnesses.TryGetValue(name, out var original) && original.Matches(mapping)) && !_slot.Witnesses(name, mapping))
                throw new IOException("extension_native_module_conflict");
        }
    }

    internal ImmutableDictionary<string, InstalledNativeMapping> Residual()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Select(module => (Name: Path.GetFileName(module.FileName),
            Mapping: new InstalledNativeMapping(InstalledNativeMapping.NormalizePath(module.FileName), module.BaseAddress.ToInt64())))
            .Where(item => _witnesses.TryGetValue(item.Name, out var original) && original.Matches(item.Mapping))
            .ToImmutableDictionary(item => item.Name, item => item.Mapping, StringComparer.OrdinalIgnoreCase);
    }

    internal bool HasUnwitnessedNative()
    {
        using var process = Process.GetCurrentProcess();
        foreach (var module in process.Modules.Cast<ProcessModule>())
        {
            var name = Path.GetFileName(module.FileName);
            if (!_paths.ContainsKey(name)) continue;
            var mapping = new InstalledNativeMapping(InstalledNativeMapping.NormalizePath(module.FileName), module.BaseAddress.ToInt64());
            if (!(_witnesses.TryGetValue(name, out var own) && own.Matches(mapping)) && !_slot.Witnesses(name, mapping)) return true;
        }
        return false;
    }

    private void ReleaseModules()
    {
        for (var index = _modules.Count - 1; index >= 0; index--) NativeLibrary.Free(_modules[index]);
        _modules.Clear(); _loaded = false;
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; ReleaseModules(); }
    }

    private static DllNotFoundException Invalid() => new("extension_native_library_not_declared");
}
