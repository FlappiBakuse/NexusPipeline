using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static class InstallerServiceHandoff
{
    internal static void EnsureOwningService(string root)
    {
        root = Path.GetFullPath(root).TrimEnd('\\');
        var owner = InstallationOwnership.Read(root, requireActive: true)
            ?? throw new IOException("installer.instance_unowned");
        if (ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(root, "data"))
            || Directory.Exists(Path.Combine(root, ".nxp-update"))
            || Directory.Exists(Path.Combine(root, ".nxp-backup"))
            || File.Exists(Path.Combine(root, ".nxp-version")))
            throw new IOException("installer.recovery_pending");
        foreach (var file in owner.PayloadFiles)
        {
            string path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            InstallationOwnership.RequireLinkFree(path);
            if (!File.Exists(path) || UpdateApply.ImageHash(path) != file.Sha256)
                throw new IOException("installer.source_payload_changed");
        }
        string executable = Path.Combine(root, "NexusPipeline.exe");
        if (!owner.PayloadFiles.Any(file => file.Path == "NexusPipeline.exe"))
            throw new IOException("installer.source_payload_missing");
        var layout = new RuntimeStateLayout(root);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(2) };
        if (IsReady(layout, executable, owner.Version, client)) return;
        // The original installer CLI waits for its receipt; only a separate owning service can release the apply worker's parent.
        using var launched = Process.Start(new ProcessStartInfo(executable, "service --background")
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new IOException("installer.owning_service_launch_failed");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (IsReady(layout, executable, owner.Version, client)) return;
            Thread.Sleep(100);
        }
        throw new IOException("installer.owning_service_unavailable");
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
