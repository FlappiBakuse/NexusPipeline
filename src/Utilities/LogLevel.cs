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

    internal static LogLevel ParseObserved(string? value, LogLevel fallback = LogLevel.Info)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return fallback;
        }
        string? token = ReadPipeToken(text) ?? ReadBracketToken(text);
        token ??= text.Split(':', 2)[0].Trim().TrimStart('[').TrimEnd(']').ToUpperInvariant();
        return token switch
        {
            "DEBUG" or "调试" => LogLevel.Debug,
            "INFO" or "INFORMATION" or "信息" => LogLevel.Info,
            "WARN" or "WARNING" or "警告" => LogLevel.Warn,
            "ERROR" or "ERR" or "错误" => LogLevel.Error,
            "FATAL" or "致命" => LogLevel.Fatal,
            _ => fallback,
        };
    }

    private static string? ReadPipeToken(string text)
    {
        if (!text.Contains('|', StringComparison.Ordinal))
        {
            return null;
        }
        foreach (string part in text.Split('|'))
        {
            string token = part.Trim().ToUpperInvariant();
            if (token is "DEBUG" or "调试" or "INFO" or "INFORMATION" or "信息"
                or "WARN" or "WARNING" or "警告" or "ERROR" or "ERR" or "错误"
                or "FATAL" or "致命")
            {
                return token;
            }
        }
        return null;
    }

    private static string? ReadBracketToken(string text)
    {
        int firstClose = text.Length > 0 && text[0] == '['
            ? text.IndexOf(']')
            : -1;
        int searchStart = firstClose >= 0 ? firstClose + 1 : 0;
        int open = text.IndexOf('[', searchStart);
        if (open < 0)
        {
            open = text.Length > 0 && text[0] == '[' ? 0 : -1;
        }
        if (open < 0)
        {
            return null;
        }
        int close = text.IndexOf(']', open + 1);
        return close > open
            ? text[(open + 1)..close].Trim().ToUpperInvariant()
            : null;
    }
}
