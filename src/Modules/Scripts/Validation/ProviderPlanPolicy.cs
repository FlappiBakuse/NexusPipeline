using System.Text;
using System.Text.Json;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Scripts.Validation;

internal static class ProviderPlanPolicy
{
    internal static void Validate(string projectRoot, PluginProviderPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.PlanId) || string.IsNullOrWhiteSpace(plan.ConfigRevision)
            || string.IsNullOrWhiteSpace(plan.AuthorizationFingerprint) || plan.Tasks.Count > 1024
            || plan.Resources.Count > 64 || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(plan)) > 1024 * 1024
            || plan.Tasks.Any(t => string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > 128 || t.Name.Length > 512)
            || plan.Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != plan.Tasks.Count)
            throw new InvalidDataException("provider.plan_shape");
        string root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/');
        foreach (var resource in plan.Resources)
        {
            if (resource.Kind == "writable_root")
            {
                string path = Path.GetFullPath(resource.Identity).TrimEnd('\\', '/');
                if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("provider.resource_scope");
            }
            else if (resource.Kind == "desktop_input")
            {
                if (resource.Identity != "current_session") throw new InvalidDataException("provider.desktop_scope");
            }
            else if (resource.Kind == "adb_endpoint")
            {
                if (resource.Identity.Length is < 1 or > 256 || resource.Identity.Any(char.IsControl))
                    throw new InvalidDataException("provider.adb_scope");
            }
            else throw new InvalidDataException("provider.resource_kind");
        }
    }
}
