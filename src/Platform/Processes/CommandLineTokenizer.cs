using System.Text.RegularExpressions;

namespace NexusPipeline.Platform.Processes;

internal static class CommandLineTokenizer
{
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
}
