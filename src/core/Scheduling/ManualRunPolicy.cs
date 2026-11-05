using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Core.Scheduling;

public static class ManualRunPolicy
{
    public static FrozenScheduledInvocation Freeze(Guid instanceId, Guid operationId, string extensionId, string providerId,
        ManualWorkTask task, DateTimeOffset startedAtUtc)
    {
        ExtensionIdentifiers.Require(extensionId); ExtensionIdentifiers.Require(providerId); ManualWorkPolicy.Validate(task);
        if (instanceId == Guid.Empty || operationId == Guid.Empty || startedAtUtc.Offset != TimeSpan.Zero ||
            startedAtUtc <= DateTimeOffset.UnixEpoch || !task.IsEnabled) throw new ArgumentException("manual_work_acceptance_invalid");
        var prefix = $"nexus.manual.work-v1\n{instanceId:D}\n{operationId:D}\n{extensionId}\n{providerId}\n";
        Guid Id(string kind) => new(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + kind)).AsSpan(0, 16));
        return new(instanceId, operationId, Id("run"), Id("attempt"), task.TaskId, Id("notification"),
            startedAtUtc.AddSeconds(task.TimeoutSeconds), task.NotificationTaskIds);
    }
}
