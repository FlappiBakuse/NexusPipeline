using System.Text;

namespace NexusPipeline;

public static class ConsoleLog
{
    private static readonly object Sync = new();

    public static string FileFor(DateTime time)
    {
        return Path.Combine(AppPaths.LogDir, $"{time:yyyy-MM-dd}.log");
    }

    public static void Write(string line)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(FileFor(DateTime.Now), line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public static void WriteSeparator(string header)
    {
        Write($"===== {DateTime.Now:HH:mm:ss} {header} =====");
    }
}
