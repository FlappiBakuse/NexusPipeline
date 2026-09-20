using System.Reflection;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Shared.Common;

/// <summary>宿主当前产品版本的共享只读值；业务模块不再从 Updates 反向读取版本。</summary>
internal sealed record HostVersionInfo(string CurrentVersion)
{
    private static readonly Lazy<HostVersionInfo> CurrentValue = new(Create);

    public static HostVersionInfo Current => CurrentValue.Value;

    private static HostVersionInfo Create()
    {
        Assembly assembly = typeof(HostVersionInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string candidate = informational.Split('+', 2)[0];
            if (NexusVersion.TryParse(candidate, out _))
            {
                return new HostVersionInfo(candidate);
            }
        }

        Version? numeric = assembly.GetName().Version;
        string fallback = numeric?.ToString(3) ?? "0.0.0";
        return new HostVersionInfo(
            NexusVersion.TryParse(fallback, out _) ? fallback : "0.0.0");
    }
}
