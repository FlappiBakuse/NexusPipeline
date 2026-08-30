using System.Text.RegularExpressions;

namespace NexusPipeline.Utilities;

internal static class TextRules
{
    public static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".cmd", ".com", ".ps1" };

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

/// <summary>自定义完成标志关键字规则：每行一组，组内逗号分隔为 AND：整个日志中分别出现即命中，跨行累积），换行之间为 OR。</summary>
internal static class KeywordRule
{
    public static List<List<string>> Parse(string text)
    {
        var groups = new List<List<string>>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return groups;
        }
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var words = line.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(word => word.Length > 0)
                .ToList();
            if (words.Count > 0)
            {
                groups.Add(words);
            }
        }
        return groups;
    }
}
