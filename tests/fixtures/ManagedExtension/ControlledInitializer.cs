using System.Text.Json;
using NexusPipeline.Extensibility.Lifecycle;

namespace Controlled;

internal sealed class ControlledInitializer : IAsyncDisposable
{
    private readonly Task _work;

    internal ControlledInitializer(ExtensionActivationContext context, CancellationToken stopping, string directory)
    {
        _work = InitializeAsync(context, stopping, directory);
    }

    private static async Task InitializeAsync(ExtensionActivationContext context, CancellationToken stopping, string directory)
    {
        File.WriteAllText(Path.Combine(directory, "readiness-started"), "started");
        try
        {
            await (context.Activation ?? throw new IOException("fixture_activation_port_missing")).WaitReadyAsync(stopping);
            var saved = await context.Documents!.ReadAsync("settings", stopping);
            File.WriteAllBytes(Path.Combine(directory, "readiness-complete.json"), JsonSerializer.SerializeToUtf8Bytes(new { revision = saved?.Revision ?? 0 }));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        { File.WriteAllText(Path.Combine(directory, "readiness-cancelled"), "cancelled"); }
    }

    public ValueTask DisposeAsync() => new(_work);
}
