using System.Text;

using System.Globalization;
using System.Runtime.CompilerServices;
using NexusPipeline.Localization;

namespace NexusPipeline.Utilities;

internal static class Logger
{
    private static readonly object Sync = new();

    /// <summary>日志阈值由宿主初始化显式配置；设置加载前使用 Info。</summary>
    private static LogLevel _currentThreshold = LogLevel.Info;

    /// <summary>CLI machine mode 下将诊断输出转到 stderr，保证 stdout 只承载协议 JSON。</summary>
    internal static bool ConsoleOutputToError { get; set; }

    /// <summary>显式设置日志阈值；调用方应在设置成功加载或保存后调用。</summary>
    public static void ConfigureLevel(string? level)
    {
        lock (Sync)
        {
            _currentThreshold = LogLevelUtil.Parse(level);
        }
    }

    private static LogLevel Threshold
    {
        get
        {
            lock (Sync)
            {
                return _currentThreshold;
            }
        }
    }

    public static void Debug(string message) => Log(LogLevel.Debug, message);

    public static void Debug(ref LocalizedLogInterpolatedStringHandler message) => LogLocalized(LogLevel.Debug, message.GetFormattedText());

    public static void Info(string message) => Log(LogLevel.Info, message);

    public static void Info(ref LocalizedLogInterpolatedStringHandler message) => LogLocalized(LogLevel.Info, message.GetFormattedText());

    public static void Warn(string message) => Log(LogLevel.Warn, message);

    public static void Warn(ref LocalizedLogInterpolatedStringHandler message) => LogLocalized(LogLevel.Warn, message.GetFormattedText());

    public static void Error(string message) => Log(LogLevel.Error, message);

    public static void Error(ref LocalizedLogInterpolatedStringHandler message) => LogLocalized(LogLevel.Error, message.GetFormattedText());

    public static void Fatal(string message) => Log(LogLevel.Fatal, message);

    public static void Fatal(ref LocalizedLogInterpolatedStringHandler message) => LogLocalized(LogLevel.Fatal, message.GetFormattedText());

    internal static bool IsEnabled(LogLevel level) => level >= Threshold;

    public static void Log(LogLevel level, string message)
    {
        if (level < Threshold)
        {
            return;
        }
        LogLocalized(level, HostLocalization.TranslateLog(message, LocaleCatalog.HostLocale));
    }

    private static void LogLocalized(LogLevel level, string message)
    {
        if (level < Threshold)
        {
            return;
        }

        string line = FormatLine(level, message);
        WriteConsole(line, level);
        lock (Sync)
        {
            try
            {
                // （P3/P10）：不依赖 Persistence.AppPaths（解除 Utilities→Persistence 反向依赖环）；
                // 日志文件按天实时求值，跨午夜自动滚动（原 static readonly 启动时固定，跨午夜写入错误文件）。
                string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                string logFile = Path.Combine(logDir, $"nexus-pipeline-{DateTime.Now:yyyy-MM-dd}.log");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    internal static string FormatLine(LogLevel level, string message)
    {
        return FormatLine(level, message, DateTimeOffset.Now);
    }

    internal static string FormatLine(LogLevel level, string message, DateTimeOffset timestamp)
    {
        return $"[{timestamp.ToLocalTime():HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}] {message}";
    }

    private static void WriteConsole(string line, LogLevel level)
    {
        try
        {
            if (ConsoleOutputToError)
            {
                Console.Error.WriteLine(line);
                return;
            }
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(line);
                return;
            }
            ConsoleColor prevForeground = Console.ForegroundColor;
            ConsoleColor prevBackground = Console.BackgroundColor;
            switch (level)
            {
                case LogLevel.Debug:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    break;
                case LogLevel.Warn:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case LogLevel.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                case LogLevel.Fatal:
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.BackgroundColor = ConsoleColor.Red;
                    break;
            }
            Console.WriteLine(line);
            Console.ForegroundColor = prevForeground;
            Console.BackgroundColor = prevBackground;
        }
        catch
        {
        }
    }
}

[InterpolatedStringHandler]
internal ref struct LocalizedLogInterpolatedStringHandler
{
    private StringBuilder _builder;

    public LocalizedLogInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _builder = new StringBuilder(literalLength);
    }

    public void AppendLiteral(string value)
    {
        _builder.Append(HostLocalization.TranslateLog(value, LocaleCatalog.HostLocale));
    }

    public void AppendFormatted<T>(T value)
    {
        _builder.Append(value);
    }

    public void AppendFormatted<T>(T value, string? format)
    {
        _builder.Append(FormatValue(value, format));
    }

    public void AppendFormatted<T>(T value, int alignment)
    {
        AppendFormatted(value, alignment, null);
    }

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        string text = FormatValue(value, format);
        _builder.Append(alignment >= 0 ? text.PadLeft(alignment) : text.PadRight(-alignment));
    }

    public string GetFormattedText() => _builder.ToString();

    private static string FormatValue<T>(T value, string? format)
    {
        if (value is IFormattable formattable)
        {
            return formattable.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
