using System.Diagnostics;

namespace NexusPipeline.Platform.Processes;

/// <summary>Read-only observation of the embedded automation runtime, never of the retained launcher.</summary>
internal static class RuntimeImageActivityProbe
{
    internal readonly record struct ImageObservation(bool Complete, IReadOnlyList<string> Images);

    internal static string Observe(string runtimeRoot,
        Func<string, ImageObservation>? observeImages = null)
    {
        try
        {
            string runtime = Path.GetFullPath(runtimeRoot);
            string[] candidates = [Path.Combine(runtime, "python.exe"), Path.Combine(runtime, "pythonw.exe")];
            if (!Directory.Exists(runtime) || candidates.All(path => !File.Exists(path))) return "unknown";
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                for (string? ancestor = candidate; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                    if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) return "unknown";
                ImageObservation observation = (observeImages ?? CaptureImages)(Path.GetFileNameWithoutExtension(candidate));
                if (!observation.Complete) return "unknown";
                foreach (string image in observation.Images)
                    if (string.Equals(Path.GetFullPath(image), candidate, StringComparison.OrdinalIgnoreCase))
                        return "active";
            }
            return "inactive";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static ImageObservation CaptureImages(string processName)
    {
        var images = new List<string>();
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (Process process in processes)
            {
                ProcessIdentity? identity = ProcessIdentity.Capture(process);
                if (identity is null || !Path.IsPathFullyQualified(identity.Value.ImageName))
                {
                    try { if (process.HasExited) continue; } catch { }
                    return new(false, []);
                }
                images.Add(identity.Value.ImageName);
            }
            return new(true, images);
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }
}
