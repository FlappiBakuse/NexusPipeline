using System.Collections.Immutable;

namespace NexusPipeline.Core.Settings;

public sealed record StartupRegistrationDefinition(Guid InstanceId, Guid RegistrationId, string UserSid,
    string ExecutablePath, ImmutableArray<string> Arguments, string WorkingDirectory, DateTimeOffset CreatedAt);
public enum StartupRegistrationState { Missing, Matches, Foreign }
public sealed record StartupRegistrationInspection(StartupRegistrationState State, string? DefinitionDigest);

public interface IStartupRegistrationInspector
{
    StartupRegistrationInspection Inspect(StartupRegistrationDefinition expected);
    void ValidateOnly(StartupRegistrationDefinition definition);
}

public sealed record StartupRegistrationCommand(StartupRegistrationDefinition Definition, string ExecutableSha256);
public enum StartupRegistrationEffect { AlreadySatisfied, Created, Removed }

public interface IStartupRegistrationEffects : IStartupRegistrationInspector
{
    StartupRegistrationEffect CreateIfMissing(StartupRegistrationCommand command);
    StartupRegistrationEffect RemoveIfMatching(StartupRegistrationDefinition expected);
}

public static class StartupRegistrationPolicy
{
    public static void Validate(StartupRegistrationCommand command)
    {
        if (command is null || command.ExecutableSha256 is not { Length: 64 } digest ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new HostSettingsException("startup_registration_executable_invalid");
        Validate(command.Definition);
    }

    public static void Validate(StartupRegistrationDefinition value)
    {
        if (value is null || value.InstanceId == Guid.Empty || value.RegistrationId == Guid.Empty || value.CreatedAt <= DateTimeOffset.UnixEpoch ||
            value.UserSid is null || value.UserSid.Length > 184 || !value.UserSid.StartsWith("S-1-", StringComparison.Ordinal) ||
            value.UserSid[4..].Split('-').Any(part => part.Length == 0 || part.Any(character => character is not (>= '0' and <= '9'))) ||
            string.IsNullOrWhiteSpace(value.ExecutablePath) || value.ExecutablePath.Length > 32700 || value.ExecutablePath.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.WorkingDirectory) || value.WorkingDirectory.Length > 32700 || value.WorkingDirectory.Any(char.IsControl) ||
            value.Arguments.IsDefault || value.Arguments.Length > 128 ||
            value.Arguments.Any(argument => argument is null || argument.Length > 4096 || argument.Any(char.IsControl)) ||
            value.Arguments.Sum(argument => argument.Length) > 16000)
            throw new HostSettingsException("startup_registration_definition_invalid");
    }
}
