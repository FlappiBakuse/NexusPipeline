namespace NexusPipeline;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

public static class LogLevelUtil
{
    public static string ToSetting(this LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => "debug",
            LogLevel.Info => "info",
            LogLevel.Warn => "warn",
            LogLevel.Error => "error",
            LogLevel.Fatal => "fatal",
            _ => "info",
        };
    }

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
