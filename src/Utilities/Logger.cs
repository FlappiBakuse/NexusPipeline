using System.Text;
using NexusPipeline.Persistence;

namespace NexusPipeline.Utilities;

internal static class Logger
{
    private static readonly object Sync = new();

    /// <summary>日志阈值缓存（v0.6.6+：避免每次日志调用访问 RuntimeContext 提前构造 DI 容器；设置加载/保存后经 <see cref="RefreshLevel"/> 失效重解析）。</summary>
    private static LogLevel? _levelCache;

    /// <summary>设置加载/保存后调用：清空阈值缓存，下次日志调用按最新配置解析（保持「阈值即时生效」契约）。</summary>
    public static void RefreshLevel()
    {
        lock (Sync)
        {
            _levelCache = null;
        }
    }

    private static LogLevel Threshold
    {
        get
        {
            LogLevel? cached = _levelCache;
            if (cached is null)
            {
                lock (Sync)
                {
                    cached = _levelCache;
                    if (cached is null)
                    {
                        cached = LogLevelUtil.Parse(RuntimeContext.Instance.Settings.LogLevel);
                        _levelCache = cached;
                    }
                }
            }
            return cached.Value;
        }
    }

    public static void Debug(string message) => Log(LogLevel.Debug, message);

    public static void Info(string message) => Log(LogLevel.Info, message);

    public static void Warn(string message) => Log(LogLevel.Warn, message);

    public static void Error(string message) => Log(LogLevel.Error, message);

    public static void Fatal(string message) => Log(LogLevel.Fatal, message);

    public static void Log(LogLevel level, string message)
    {
        if (level < Threshold)
        {
            return;
        }
        string line = $"[{DateTime.Now:HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}";
        WriteConsole(line, level);
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    private static void WriteConsole(string line, LogLevel level)
    {
        try
        {
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
