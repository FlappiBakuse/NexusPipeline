using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Desktop;

internal static class DesktopActivationClient
{
    internal static async Task<bool> ShowAsync()
    {
        try
        {
            string hash = DesktopSupervisorProtocol.RootHash(AppPaths.AppRoot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", DesktopSupervisorProtocol.PipeName(hash), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            DesktopProcessIdentity peer = NamedPipePeerIdentity.Server(pipe);
            if (!string.Equals(peer.ExecutablePath, Path.Combine(AppPaths.AppRoot, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            await DesktopPipeTransport.WriteAsync(pipe, new { type = "hello", requestId = "activate", data = new { role = "activation", protocol = 1, rootHash = hash, generation = "g0170", sessionId = "", clientNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() } }, timeout.Token);
            JsonElement attached = await DesktopPipeTransport.ReadAsync(pipe, timeout.Token);
            DesktopSupervisorProtocol.Fields(attached, "type", "requestId", "data");
            JsonElement identity = attached.GetProperty("data");DesktopSupervisorProtocol.Fields(identity, "hostPid", "hostStartFileTime");
            if (attached.GetProperty("type").GetString() != "activation-attached" || identity.GetProperty("hostPid").GetInt32() != peer.Pid || identity.GetProperty("hostStartFileTime").GetString() != peer.StartFileTime) return false;
            await DesktopPipeTransport.WriteAsync(pipe, new { type = "window.show", requestId = "activate", data = new { } }, timeout.Token);
            JsonElement result = await DesktopPipeTransport.ReadAsync(pipe, timeout.Token);
            DesktopSupervisorProtocol.Fields(result, "type", "requestId", "data");
            return result.GetProperty("type").GetString() == "window.show-result";
        }
        catch { return false; }
    }
}
