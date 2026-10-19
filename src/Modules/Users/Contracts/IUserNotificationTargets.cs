namespace NexusPipeline.Modules.Users.Contracts;

internal interface IUserNotificationTargets
{
    string? Validate(IEnumerable<string>? ids, IEnumerable<string>? previous = null);
}
