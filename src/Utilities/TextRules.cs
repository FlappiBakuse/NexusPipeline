using System.Text.RegularExpressions;

namespace NexusPipeline.Utilities;

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

/// <summary>自定义完成标志关键字规则：每行一组，组内逗号分隔为 AND（同一行内全部词出现才命中），换行之间为 OR。</summary>
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

    /// <summary>单行是否命中任一组：组内 AND（全部词都出现在该行），组间 OR（任一组命中即命中）。</summary>
    public static bool LineHits(string line, List<List<string>> groups)
    {
        if (string.IsNullOrWhiteSpace(line) || groups.Count == 0)
        {
            return false;
        }
        foreach (List<string> words in groups)
        {
            bool all = true;
            foreach (string word in words)
            {
                if (!line.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    all = false;
                    break;
                }
            }
            if (all)
            {
                return true;
            }
        }
        return false;
    }
}
