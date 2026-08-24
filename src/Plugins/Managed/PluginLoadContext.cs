using System.Reflection;
using System.Runtime.Loader;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins.Managed;

/// <summary>每个 managed-code 插件独立的依赖加载边界；Plugin API 程序集始终复用宿主实例。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryAssemblyPath)
        : base($"NexusPipeline.Plugin:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    /// <summary>从流加载入口程序集，避免 Windows 在运行期间锁住插件目录中的可替换文件。</summary>
    public Assembly LoadEntryAssembly(string entryAssemblyPath)
    {
        using FileStream stream = File.OpenRead(entryAssemblyPath);
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, typeof(INexusPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return typeof(INexusPlugin).Assembly;
        }
        string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolved is null ? null : LoadFromAssemblyPath(resolved);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return resolved is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(resolved);
    }
}
