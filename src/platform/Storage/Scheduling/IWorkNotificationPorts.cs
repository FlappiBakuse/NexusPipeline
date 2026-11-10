using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using CoreSubmission = NexusPipeline.Core.Notifications.NotificationSubmission;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal interface IWorkNotificationPorts
{
    NotificationSubmissionPort CreateScheduled(ExtensionManifest manifest, ExtensionScope scope, Func<CoreSubmission, WorkNotificationOrigin> bind);
    NotificationSubmissionPort CreateManual(ExtensionManifest manifest, ExtensionScope scope, Func<CoreSubmission, WorkNotificationOrigin> bind);
}
