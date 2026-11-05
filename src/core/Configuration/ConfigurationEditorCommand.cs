using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationEditorCommand(string ExecutablePath, string ExecutableSha256,
    ImmutableArray<string> Arguments, string WorkingDirectory)
{
    public bool ExclusiveProgram { get; init; }
}

public static class EditorCommandPolicy
{
    public static ConfigurationEditorCommand Freeze(ConfigurationEditorCommand command)
    {
        if (command is null || !PathValue(command.ExecutablePath) || !PathValue(command.WorkingDirectory) ||
            !FileTreePolicy.Digest(command.ExecutableSha256) || command.Arguments.IsDefault || command.Arguments.Length > 256 ||
            command.Arguments.Any(value => value is null || value.Length > 32767 || value.Any(char.IsControl)) ||
            command.Arguments.Sum(value => (long)value.Length + 3) > 30000)
            throw new ConfigurationException("configuration_editor_command_invalid");
        return command with { Arguments = command.Arguments.ToImmutableArray() };
    }

    public static bool Equal(ConfigurationEditorCommand first, ConfigurationEditorCommand second) =>
        first.ExecutablePath == second.ExecutablePath && first.ExecutableSha256 == second.ExecutableSha256 &&
        first.ExclusiveProgram == second.ExclusiveProgram && first.WorkingDirectory == second.WorkingDirectory && first.Arguments.SequenceEqual(second.Arguments);

    private static bool PathValue(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 4 and <= 32767 && !value.Any(char.IsControl);
}
