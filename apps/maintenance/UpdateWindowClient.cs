using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class UpdateWindowClient(NamedPipeClientStream pipe, SafeProcessHandle host, UpdateHostImage image) : IDisposable
{
    internal static async Task<UpdateWindowClient> OpenAsync(CancellationToken cancellation)
    {
        var root = UpdateHostImage.ApplicationRoot();
        var connection = UpdateWindowPipe.Connect(root);
        UpdateHostImage? verified = null;
        try
        {
            verified = UpdateHostImage.Hold(root);
            try { await connection.ConnectAsync(250, cancellation); }
            catch (TimeoutException)
            {
                var start = new ProcessStartInfo(verified.Executable)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root
                };
                start.ArgumentList.Add("update-session");
                start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                using var process = Process.Start(start) ?? throw new IOException("maintenance_window_host_unavailable");
                await connection.ConnectAsync(20000, cancellation);
            }
            var host = verified.Bind(UpdateWindowPipe.ServerProcess(connection));
            return new(connection, host, verified);
        }
        catch { connection.Dispose(); verified?.Dispose(); throw; }
    }

    internal Task<UpdateWindowStatus> ExecuteAsync(UpdateWindowCommand command, CancellationToken cancellation)
    {
        if (!UpdateHostImage.IsLive(host)) throw new IOException("maintenance_window_host_unavailable");
        return UpdateWindowPipe.RequestAsync(pipe, command, cancellation);
    }

    public void Dispose() { pipe.Dispose(); host.Dispose(); image.Dispose(); }
}
