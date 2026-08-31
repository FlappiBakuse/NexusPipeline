namespace NexusPipeline.Models;

internal static class UserNameRule
{
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name != name.Trim()
            || name.EndsWith(".", StringComparison.Ordinal)
            || name.EndsWith(" ", StringComparison.Ordinal)
            || name is "." or "..")
        {
            return false;
        }
        string deviceName = name.Split('.')[0];
        return !deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            && !deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(deviceName, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
