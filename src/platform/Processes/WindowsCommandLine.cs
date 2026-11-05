using System.Text;

namespace NexusPipeline.Platform.Processes;

internal static class WindowsCommandLine
{
    internal static StringBuilder Build(string executable, IReadOnlyList<string> arguments, CommandLineSyntax syntax)
    {
        if (!Enum.IsDefined(syntax)) throw new ArgumentException("worker_command_line_syntax_invalid");
        if (syntax == CommandLineSyntax.ArgumentVector)
            return new(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        var expected = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(executable), expected) || arguments.Count != 5 ||
            arguments[0] != "/d" || arguments[1] != "/s" || arguments[2] != "/v:off" || arguments[3] != "/c" ||
            arguments[4].Length < 4 || !arguments[4].StartsWith("\"\"", StringComparison.Ordinal) || !arguments[4].EndsWith("\"\"", StringComparison.Ordinal) ||
            arguments[4].Any(char.IsControl) || arguments[4].Contains('%'))
            throw new ArgumentException("worker_command_interpreter_unbound");
        // /s consumes the outer quotes itself; CRT escaping would change the script command.
        var line = new StringBuilder(Quote(executable)).Append(" /d /s /v:off /c ").Append(arguments[4]);
        if (line.Length > 8191) throw new ArgumentException("worker_command_interpreter_limit");
        return line;
    }

    internal static string Quote(string value)
    {
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("Process arguments cannot contain NUL");
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            output.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            output.Append(character);
            slashes = 0;
        }
        output.Append('\\', slashes * 2);
        return output.Append('"').ToString();
    }
}
