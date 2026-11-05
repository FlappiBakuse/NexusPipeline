using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml.Linq;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationEffects : IStartupRegistrationEffects
{
    private readonly Func<IStartupTaskScheduler> _open;
    public StartupRegistrationEffects() : this(() => new WindowsTaskScheduler()) { }
    public StartupRegistrationEffects(Func<IStartupTaskScheduler> open) => _open = open;

    public StartupRegistrationInspection Inspect(StartupRegistrationDefinition expected)
    {
        using var session = _open();
        return Inspect(session, expected);
    }

    public void ValidateOnly(StartupRegistrationDefinition definition)
    {
        RequireCurrentUser(definition);
        using var session = _open();
        var name = StartupTaskXml.TaskName(definition);
        var before = session.Read(name);
        session.Validate(name, StartupTaskXml.Encode(definition), definition.UserSid);
        if (session.Read(name) != before) throw new IOException("startup_validation_changed_registration");
    }

    public StartupRegistrationEffect CreateIfMissing(StartupRegistrationCommand command)
    {
        StartupRegistrationPolicy.Validate(command);
        var definition = command.Definition;
        RequireCurrentUser(definition);
        var executable = PhysicalPathPolicy.NormalizeWithoutLinks(definition.ExecutablePath);
        using var directory = StableDirectoryLease.Acquire(Path.GetDirectoryName(executable)!);
        using var working = StableDirectoryLease.Acquire(definition.WorkingDirectory);
        using var image = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(image)) != command.ExecutableSha256)
            throw new IOException("startup_registration_executable_changed");
        using var session = _open();
        var before = Inspect(session, definition);
        if (before.State == StartupRegistrationState.Matches) return StartupRegistrationEffect.AlreadySatisfied;
        if (before.State != StartupRegistrationState.Missing) throw new IOException("startup_registration_foreign");
        var xml = StartupTaskXml.Encode(definition);
        session.Validate(StartupTaskXml.TaskName(definition), xml, definition.UserSid);
        if (Inspect(session, definition).State != StartupRegistrationState.Missing) throw new IOException("startup_registration_changed_before_effect");
        session.Create(StartupTaskXml.TaskName(definition), xml, definition.UserSid);
        if (Inspect(session, definition).State != StartupRegistrationState.Matches) throw new IOException("startup_registration_outcome_unverified");
        return StartupRegistrationEffect.Created;
    }

    public StartupRegistrationEffect RemoveIfMatching(StartupRegistrationDefinition expected)
    {
        RequireCurrentUser(expected);
        using var session = _open();
        var before = Inspect(session, expected);
        if (before.State == StartupRegistrationState.Missing) return StartupRegistrationEffect.AlreadySatisfied;
        if (before.State != StartupRegistrationState.Matches) throw new IOException("startup_registration_foreign");
        if (Inspect(session, expected) != before) throw new IOException("startup_registration_changed_before_effect");
        // Task Scheduler has no compare-and-delete; the instance lease and same-user trust boundary serialize cooperating owners.
        session.Delete(StartupTaskXml.TaskName(expected));
        if (session.Read(StartupTaskXml.TaskName(expected)) is not null) throw new IOException("startup_registration_outcome_unverified");
        return StartupRegistrationEffect.Removed;
    }

    private static StartupRegistrationInspection Inspect(IStartupTaskScheduler session, StartupRegistrationDefinition expected)
    {
        var wanted = StartupTaskXml.Encode(expected);
        var actual = session.Read(StartupTaskXml.TaskName(expected));
        if (actual is null) return new(StartupRegistrationState.Missing, null);
        var normalized = session.Normalize(actual);
        var state = XNode.DeepEquals(StartupTaskXml.Read(session.Normalize(wanted)), StartupTaskXml.Read(normalized))
            ? StartupRegistrationState.Matches : StartupRegistrationState.Foreign;
        return new(state, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))));
    }

    private static void RequireCurrentUser(StartupRegistrationDefinition definition)
    {
        StartupRegistrationPolicy.Validate(definition);
        using var user = WindowsIdentity.GetCurrent();
        if (user.User?.Value != definition.UserSid) throw new IOException("startup_registration_user_unbound");
    }
}
