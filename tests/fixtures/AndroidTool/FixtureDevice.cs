namespace NexusPipeline.AndroidToolFixture;

internal sealed record Device(string Driver, string Endpoint, int Port, int Index, string Package, string Nonce,
    bool Online, string? Foreground, string? FailAction = null, string? OverflowAction = null,
    string? ServerAction = null, int DelayMilliseconds = 0, int? ReportedPort = null, string? ResponseAction = null, string? ResponseStandardOutput = null,
    string? ResponseStandardError = null, int ResponseExitCode = 0, bool HangAfterResponse = false, int ResponsePaddingBytes = 0, string? ScreenshotMode = null);
internal sealed record Invocation(string[] Arguments, string WorkingDirectory, int ProcessId, string Nonce);
