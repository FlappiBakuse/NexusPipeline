namespace NexusPipeline.Modules.Execution.Realtime;

internal static class RealtimeEventNames
{
    internal const string RunStatus = "run.status";

    internal const string RunLog = "run.log";
    internal const string TaskReportChanged = "task-report-changed";

    internal const string SystemAction = "system.action";

    internal const string HostStatus = "host.status";

    internal const string StreamReady = "stream.ready";

    internal const string StreamMissed = "stream.missed";
}

/// <summary>SSE data 的稳定包络；事件类型由 SSE event 字段承载。</summary>
internal sealed record RealtimeEventEnvelope(
    int SchemaVersion,
    long Sequence,
    DateTimeOffset Timestamp,
    System.Text.Json.JsonElement Data);

internal sealed record RealtimeEvent(string Type, RealtimeEventEnvelope Envelope);
