using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

internal static class TaskProtocolScriptRunner
{
    internal static async Task<T> ExecuteAsync<T>(string source, object input,
        Func<string, string> readConfig, Func<string, string> readResource,
        bool preview, CancellationToken token,
        Func<string, TaskEnvironmentInspection>? inspectDeclaredTarget = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(preview ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30));
        return await Task.Run(() =>
        {
            var host = JintScriptHost.Create(preview ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30), deadline.Token, 2_000_000, 128 * 1024 * 1024);
            host.SetInput(TaskProtocolJson.Write(input));
            host.SetValue("__nexusReadConfig", WrapRead(readConfig));
            host.SetValue("__nexusReadResource", WrapRead(readResource));
            host.SetValue("__nexusInspectDeclaredTarget", WrapInspection(inspectDeclaredTarget));
            host.Execute(source, JintScriptHostProfile.TaskProtocol);
            if (host.Outputs.Count != 1) throw new InvalidDataException("protocol_error: expected exactly one result");
            return TaskProtocolJson.Read<T>(host.Outputs[0]);
        }, deadline.Token).ConfigureAwait(false);
    }

    // Resource absence is a catchable JS error; CLR objects and local paths never cross the boundary.
    private static Func<string, string> WrapRead(Func<string, string> read) => id =>
    {
        try { return "{\"ok\":true,\"value\":" + read(id) + "}"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return "{\"ok\":false}"; }
    };

    private static Func<string, string> WrapInspection(Func<string, TaskEnvironmentInspection>? inspect) => id =>
    {
        try
        {
            TaskEnvironmentInspection result = inspect?.Invoke(id)
                ?? new TaskEnvironmentInspection(id, "not_checked", "unsupported", null, null, "inspection_unavailable");
            return "{\"ok\":true,\"value\":" + TaskProtocolJson.Write(result) + "}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return "{\"ok\":false}"; }
    };
}
