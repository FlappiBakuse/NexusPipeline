using System.Text;

namespace NexusPipeline;

public static class Logger
{
    private static readonly object Sync = new();

    public static void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
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
}
