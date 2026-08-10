namespace NexusPipeline.Models;

public class ScriptUser
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string PreRunScript { get; set; } = "";

    public bool PreRunOnceOnly { get; set; }

    public string PostRunScript { get; set; } = "";

    public bool PostRunOnFinalOnly { get; set; }

    public ScriptUser Clone()
    {
        return new ScriptUser
        {
            Name = Name,
            Enabled = Enabled,
            PreRunScript = PreRunScript,
            PreRunOnceOnly = PreRunOnceOnly,
            PostRunScript = PostRunScript,
            PostRunOnFinalOnly = PostRunOnFinalOnly,
        };
    }
}

internal static class ScriptUserRule
{
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && name != "."
            && name != "..";
    }
}
