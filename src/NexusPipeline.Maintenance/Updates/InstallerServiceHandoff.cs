using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static class InstallerServiceHandoff
{
    internal static int? FindOwningServicePort(string root, string version)
    {
        var layout = new RuntimeStateLayout(root);
        InstallationOwnership.RequireLinkFree(layout.WebPortPath);
        if (!File.Exists(layout.WebPortPath) || !int.TryParse(File.ReadAllText(layout.WebPortPath).Trim(),
            NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535) return null;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(2) };
        return IsReady(layout, Path.Combine(root, "NexusPipeline.exe"), version, client) ? port : null;
    }

    private static bool IsReady(RuntimeStateLayout layout, string executable, string version, HttpClient client)
    {
        try
        {
            InstallationOwnership.RequireLinkFree(layout.ServicePidPath);
            InstallationOwnership.RequireLinkFree(layout.WebPortPath);
            if (!int.TryParse(File.ReadAllText(layout.ServicePidPath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
                || !int.TryParse(File.ReadAllText(layout.WebPortPath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535) return false;
            using var process = Process.GetProcessById(pid);
            var identity = ProcessIdentity.Capture(process);
            if (identity is null || !identity.Value.ImageName.Equals(executable, StringComparison.OrdinalIgnoreCase)) return false;
            using var response = client.GetAsync($"http://127.0.0.1:{port}/api/status?view=identity").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            var status = document.RootElement;
            var current = ProcessIdentity.Capture(process);
            return current is not null && identity.Value.Matches(current.Value)
                && File.ReadAllText(layout.ServicePidPath).Trim() == pid.ToString(CultureInfo.InvariantCulture)
                && status.GetProperty("ready").GetBoolean()
                && status.GetProperty("version").GetString() == version
                && status.GetProperty("installationGeneration").GetString() == InstallationGeneration.Id
                && status.GetProperty("actualPort").GetInt32() == port;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { return false; }
    }
}
