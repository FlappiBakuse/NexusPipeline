using System.Text.RegularExpressions;
namespace NexusPipeline.Modules.Execution.Judgement;


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
