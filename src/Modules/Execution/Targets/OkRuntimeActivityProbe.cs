using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Modules.Execution.Targets;

/// <summary>Supported upstream layout selection belongs to execution, not the platform image probe.</summary>
internal static class OkRuntimeActivityProbe
{
    internal static string Observe(string pluginType, string scriptRoot,
        Func<string, RuntimeImageActivityProbe.ImageObservation>? observeImages = null)
    {
        string app = pluginType switch { "oknte" => "ok-nte", "okww" => "ok-ww", _ => "" };
        if (app.Length == 0 || string.IsNullOrWhiteSpace(scriptRoot)) return "unknown";
        return RuntimeImageActivityProbe.Observe(Path.Combine(scriptRoot, "data", "apps", app, "python"), observeImages);
    }
}
