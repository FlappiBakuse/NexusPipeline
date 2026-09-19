namespace NexusPipeline.Shared.Text;

internal static class TextTail
{
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
