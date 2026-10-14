using NexusPipeline.Modules.Scripts.Contracts;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Scripts.UseCases;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("provider-configurations")]
internal static class ApiProviderConfigurationsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body,
        IProviderConfigurationOperations operations, ScriptCommands scripts)
    {
        try
        {
            if (method == "GET" && seg.Length == 1)
            {
                await HttpHelper.WriteJsonAsync(context, operations.Snapshot()).ConfigureAwait(false); return;
            }
            if (method == "GET" && seg.Length == 2)
            {
                var status = operations.Find(seg[1]);
                if (status is null) { await HttpHelper.NotFoundAsync(context).ConfigureAwait(false); return; }
                await HttpHelper.WriteJsonAsync(context, status).ConfigureAwait(false); return;
            }
            if (method == "POST" && seg.Length == 3 && seg[2] == "cancel")
            {
                await HttpHelper.WriteJsonAsync(context, new { requested = operations.Cancel(seg[1]) }).ConfigureAwait(false); return;
            }
            if (method == "POST" && seg.Length == 1)
            {
                var request = JsonSerializer.Deserialize<ProviderConfigurationSaveRequest>(body, ProviderConfigurationContract.Json)
                    ?? throw new InvalidDataException("configuration.request.invalid");
                var status = operations.Start(request, scripts.CommitProviderConfiguration);
                await HttpHelper.WriteJsonAsync(context, status, 202).ConfigureAwait(false); return;
            }
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
        {
            await HttpHelper.ErrorAsync(context, e is InvalidDataException ? e.Message : "configuration.request.invalid", 400).ConfigureAwait(false);
        }
    }
}
