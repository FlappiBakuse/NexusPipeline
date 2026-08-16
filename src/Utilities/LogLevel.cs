namespace NexusPipeline.Utilities;

internal enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

internal static class LogLevelUtil
{
    public static LogLevel Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warn" => LogLevel.Warn,
            "error" => LogLevel.Error,
            "fatal" => LogLevel.Fatal,
            _ => LogLevel.Info,
        };
    }

    public static bool IsValid(string? value)
    {
        return value is "debug" or "info" or "warn" or "error" or "fatal";
    }
}
