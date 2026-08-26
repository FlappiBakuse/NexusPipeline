namespace NexusPipeline.Cli;

/// <summary>轻量 CLI 参数解析器。保留原命令的短选项，同时支持 noun/subcommand 形式。</summary>
internal sealed class CliArguments
{
    private CliArguments(List<string> positionals, Dictionary<string, string?> options, bool help)
    {
        Positionals = positionals;
        Options = options;
        HelpRequested = help;
    }

    public IReadOnlyList<string> Positionals { get; }

    public IReadOnlyDictionary<string, string?> Options { get; }

    public bool HelpRequested { get; }

    public bool Has(string name) => Options.ContainsKey(NormalizeName(name));

    public bool TryGet(string name, out string? value) => Options.TryGetValue(NormalizeName(name), out value);

    public string? Get(string name) => Options.TryGetValue(NormalizeName(name), out string? value) ? value : null;

    public static bool TryParse(
        IEnumerable<string> rawArgs,
        out CliArguments? result,
        out string? error)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        bool help = false;
        string[] args = rawArgs.Where(argument =>
                !string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (token is "--help" or "-h")
            {
                help = true;
                continue;
            }
            if (!token.StartsWith("-", StringComparison.Ordinal) || token == "-")
            {
                positionals.Add(token);
                continue;
            }

            int separator = token.IndexOf('=');
            string name = separator > 0 ? token[..separator] : token;
            string? value = separator > 0 ? token[(separator + 1)..] : null;
            name = NormalizeName(name);
            if (name.Length == 0)
            {
                result = null;
                error = "选项名称不能为空";
                return false;
            }

            if (separator < 0 && index + 1 < args.Length
                && (!args[index + 1].StartsWith("-", StringComparison.Ordinal) || args[index + 1] == "-"))
            {
                value = args[++index];
            }
            options[name] = value;
        }

        result = new CliArguments(positionals, options, help);
        error = null;
        return true;
    }

    public static string NormalizeName(string name)
    {
        string normalized = name.Trim();
        while (normalized.StartsWith("-", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }
        return normalized.ToLowerInvariant();
    }
}
