using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;

if (args.Length == 2 && args[0] == "--owned-window")
{
    if (!File.Exists(".nxp-test-fixture") || File.ReadAllText(".nxp-test-fixture") != "owned-process-contract"
        || !int.TryParse(args[1], out int delay) || delay is < 0 or > 10000) return 4;
    string root = Environment.CurrentDirectory;
    var thread = new Thread(() =>
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < delay)
        {
            if (File.Exists(Path.Combine(root, "stop-window"))) return;
            Thread.Sleep(20);
        }
        using var window = new System.Windows.Forms.Form { Text = "Nexus owned readiness fixture", Width = 300, Height = 200 };
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => { if (File.Exists(Path.Combine(root, "stop-window"))) window.Close(); };
        window.Shown += (_, _) => { File.WriteAllText(Path.Combine(root, "window-ready"), "owned-window"); timer.Start(); };
        System.Windows.Forms.Application.Run(window);
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    return 0;
}

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
if (bootstrap.Input["progressCount"]?.GetValue<int>() is int count)
{
    if (count is < 0 or > 10000) return 66;
    for (int index = 0; index < count; index++) await Send("progress", new() { ["code"] = "fixture.progress" });
}
await Send("task_event", new() { ["taskId"] = "task", ["status"] = "succeeded" });
await Send("completed", new() { ["status"] = "succeeded", ["producedAtTicks"] = System.Diagnostics.Stopwatch.GetTimestamp() });
return 0;
