using NexusPipeline.Extensibility.Emulation;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class EmulatorHostFixture(EmulatorTarget target) : IEmulatorHost
{
    internal readonly List<EmulatorCommand> Commands = [];
    internal int Cancelled;
    public ValueTask<EmulatorCommandResult> RunAsync(EmulatorTarget value, EmulatorCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(target.Id, value.Id);
        Commands.Add(command); return ValueTask.FromResult(new EmulatorCommandResult("completed", 0, "", "", "action:" + Commands.Count));
    }
    public ValueTask<EmulatorRefresh> RefreshAsync(EmulatorTarget value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EmulatorRefresh(target.Installation, target.Instance, [], "physical-generation"));
    public ValueTask<EmulatorAdbSession> ConnectAsync(EmulatorTarget value, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EmulatorAdbSession("controlled-session", target.Id, target.Endpoint, "controlled-device", true, null, "physical-generation"));
    public ValueTask<EmulatorProbe> ProbeAsync(EmulatorAdbSession session, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EmulatorProbe("online", session.Id, session.DeviceIdentity));
    public ValueTask<EmulatorCapture> CaptureAsync(EmulatorAdbSession session, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EmulatorCapture("captured", "controlled-image", "image/png", 640, 480));
    public ValueTask<bool> ShutdownAsync(EmulatorAdbSession session, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    public ValueTask<bool> OfflineAsync(EmulatorAdbSession session, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    public ValueTask<bool> StopAsync(EmulatorTarget value, string processEvidenceId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("fixture_process_stop_unexpected");
    public ValueTask CancelAsync(string actionId, CancellationToken cancellationToken)
    {
        Assert.Equal("action:" + Commands.Count, actionId); Cancelled++; return ValueTask.CompletedTask;
    }
}
