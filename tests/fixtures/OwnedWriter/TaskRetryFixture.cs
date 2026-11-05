using System.Text.Json;
using NexusPipeline.Extensibility.Execution;

internal static class TaskRetryFixture
{
    internal static async Task RunAsync(ExecutionProviderChannel channel, string marker, CancellationToken cancellation)
    {
        var path = Path.Combine(channel.Bootstrap.ConfigurationTargets.Single(), "local.json");
        var bytes = await File.ReadAllBytesAsync(path, cancellation);
        using var input = JsonDocument.Parse(bytes);
        var value = input.RootElement;
        var history = marker + ".tasks";
        var first = !File.Exists(history);
        if (value.GetProperty("account").GetString() != "saved-account" || !value.GetProperty("unlisted").GetBoolean() ||
            value.GetProperty("counter").GetInt32() != 17 || value.GetProperty("taskA").GetBoolean() != first ||
            !value.GetProperty("taskB").GetBoolean()) throw new IOException("fixture_task_projection_invalid");
        await File.AppendAllTextAsync(history, System.Text.Encoding.UTF8.GetString(bytes) + Environment.NewLine, cancellation);
        if (first) await Console.Out.WriteLineAsync("fixture:taskA:succeeded");
        await Console.Out.WriteLineAsync("fixture:taskB:" + (first ? "failed" : "succeeded"));
        await Console.Out.FlushAsync();
        await channel.PublishTerminalAsync(first ? "failed" : "succeeded", "fixture_task_complete", cancellation);
    }
}
