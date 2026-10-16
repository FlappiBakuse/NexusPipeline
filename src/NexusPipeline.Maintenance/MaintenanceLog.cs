namespace NexusPipeline.Maintenance;
internal static class MaintenanceLog
{
    internal static Action<string, string>? Sink { get; set; }
    internal static string? Root { get; set; }
    private static readonly object Gate = new();
    internal static void Info(string message) => Write("info", message);
    internal static void Warn(string message) => Write("warn", message);
    internal static void Error(string message) => Write("error", message);
    internal static void Debug(string message) => Write("debug", message);
    private static void Write(string level, string message)
    {
        if (Sink is { } sink) { sink(level, message); return; }
        Console.Error.WriteLine(message);
        if (Root is { } root) lock (Gate)
        {
            try
            {
                string logs = Path.Combine(root, "logs");
                NexusPipeline.Platform.Storage.PayloadPathSafety.RequireLinkFree(logs);
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, $"nexus-pipeline-{DateTime.Now:yyyy-MM-dd}.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [{level.ToUpperInvariant()}] {message}" + Environment.NewLine);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
internal static class MaintenanceAudit
{
    internal const string System = "system";
    internal static void Log(string source, string action, string detail = "") => MaintenanceLog.Info($"[审计] {source} | {action}（{detail}）");
}
