using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;

if (args.SequenceEqual(new[] { "--owned-lifetime" }))
{
    if (!File.Exists(".nxp-test-fixture") || File.ReadAllText(".nxp-test-fixture") != "owned-process-contract") return 4;
    Console.WriteLine("owned-ready");
    await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
    return 0;
}

Console.Error.WriteLine("fixture waiting bootstrap");
var bootstrap = JsonSerializer.Deserialize<PluginWorkerBootstrap>((await Console.In.ReadLineAsync())!, PluginWorkerProtocol.Json)!;
Console.Error.WriteLine("fixture bootstrap read");
using var events = new NamedPipeClientStream(".", bootstrap.EventPipe, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var control = new NamedPipeClientStream(".", bootstrap.ControlPipe, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await Task.WhenAll(events.ConnectAsync(), control.ConnectAsync());
Console.Error.WriteLine("fixture pipes connected");
long sequence = 0;
ValueTask Send(string kind, JsonObject payload) => PluginWorkerProtocol.WriteAsync(events,
    new(1, bootstrap.ExecutionId, bootstrap.RecordId, bootstrap.AttemptNumber, bootstrap.SessionId,
        "worker_to_host", ++sequence, kind, payload), CancellationToken.None);
await Send("handshake", new() { ["nonce"] = bootstrap.Input["wrongNonce"]?.GetValue<bool>() == true ? "wrong" : bootstrap.Nonce });
var start = await PluginWorkerProtocol.ReadAsync(control, CancellationToken.None);
PluginWorkerProtocol.Validate(start, bootstrap, "host_to_worker", 1);
if (bootstrap.Input["waitForCancel"]?.GetValue<bool>() == true || bootstrap.Input["neverReady"]?.GetValue<bool>() == true)
{
    var cancel = await PluginWorkerProtocol.ReadAsync(control, CancellationToken.None);
    PluginWorkerProtocol.Validate(cancel, bootstrap, "host_to_worker", 2);
    await Send("cancel_ack", new() { ["status"] = "cancelled" });
    return 2;
}
await Send("ready", new());
await Send("task_event", new() { ["taskId"] = "task", ["status"] = "running" });
await Send("task_event", new() { ["taskId"] = "task", ["status"] = "succeeded" });
await Send("completed", new() { ["status"] = "succeeded" });
return 0;
