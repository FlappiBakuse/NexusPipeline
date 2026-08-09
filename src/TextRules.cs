using System.Text.RegularExpressions;

namespace NexusPipeline;

internal static class TextRules
{
    public static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".cmd", ".com", ".ps1" };

    public static bool Contains(string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }
        return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool LineHasCompletionMarker(string line, IEnumerable<string> markers)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        foreach (string marker in markers)
        {
            if (!string.IsNullOrWhiteSpace(marker) && line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ExecutableExtensions.Contains(ext);
    }

    public static List<string> SplitArgs(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args))
        {
            return result;
        }
        foreach (Match match in Regex.Matches(args, @"(""[^""]*"")|(\S+)"))
        {
            result.Add(match.Value.Trim('"'));
        }
        return result;
    }

    public static List<string> GetErrorLines(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return new List<string>();
        }
        List<string> lines = logText.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        List<string> withTime = lines
            .Where(line => Regex.IsMatch(line, @"(\d{2}:\d{2}:\d{2}|\[\d{4}-\d{2}-\d{2})"))
            .Where(line => Regex.IsMatch(line, "ERROR|错误|异常|失败|Error|Exception"))
            .ToList();
        if (withTime.Count == 0)
        {
            withTime = lines.Where(line => Regex.IsMatch(line, "ERROR|错误|异常|失败|Error|Exception")).ToList();
        }
        return withTime.Where(line => !string.IsNullOrWhiteSpace(line)).Take(12).ToList();
    }

    public static string TakeTail(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        List<string> lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0).ToList();
        return string.Join("\n", lines.TakeLast(maxLines));
    }
}
