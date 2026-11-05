using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

internal sealed class PluginSliceAssembly : IDisposable
{
    private readonly FileStream _assemblyLease, _manifestLease;
    internal byte[] ManifestBytes { get; }
    internal ExtensionManifest Manifest { get; }
    internal Assembly Assembly { get; }
    internal Type ExtensionType { get; }
    internal string AssemblySha256 { get; }
    internal string[] References { get; }

    internal PluginSliceAssembly(string assemblyPath, string expectedSha256, string manifestPath, string extensionId, ContractReader reader)
    {
        _assemblyLease = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _manifestLease = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                AssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(_assemblyLease));
                if (AssemblySha256 != expectedSha256) throw new IOException("Plugin slice assembly changed");
                ManifestBytes = File.ReadAllBytes(manifestPath);
                Manifest = new ExtensionManifestAdmission(reader).ReadCurrent(ManifestBytes, extensionId);
                if (Path.GetFileName(assemblyPath) != Manifest.Managed!.EntryAssembly) throw new IOException("Plugin slice entry differs");
                Assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                References = Assembly.GetReferencedAssemblies().Select(item => item.Name!).Order().ToArray();
                if (!References.Contains("NexusPipeline.Extensibility") || References.Any(name => name != "NexusPipeline.Extensibility" && !name.StartsWith("System.", StringComparison.Ordinal)))
                    throw new IOException("Plugin slice references private or undeclared assemblies");
                ExtensionType = Assembly.GetType(Manifest.Managed.EntryType, throwOnError: true)!;
            }
            catch { _manifestLease.Dispose(); throw; }
        }
        catch { _assemblyLease.Dispose(); throw; }
    }

    public void Dispose()
    {
        _manifestLease.Dispose();
        _assemblyLease.Dispose();
    }
}
