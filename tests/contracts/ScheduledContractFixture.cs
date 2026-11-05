using System.Text.Json;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using WireSubmission = NexusPipeline.Extensibility.Protocol.NotificationSubmission;
using CoreSubmission = NexusPipeline.Core.Notifications.NotificationSubmission;

namespace NexusPipeline.ContractTests;

internal sealed class ScheduledContractFixture
{
    internal FrozenScheduledInvocation Invocation { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), [Guid.NewGuid(), Guid.NewGuid()]);
    internal Policy Authorization { get; }
    internal Queue Notifications { get; } = new();
    internal ScheduledWorkRegistry Registry { get; }
    internal ExtensionScope? Scope { get; private set; }
    internal ScheduleRegistrationPort? Schedules { get; private set; }
    internal NotificationSubmissionPort? NotificationPort { get; private set; }
    internal Func<ScheduledInvocation, CancellationToken, ValueTask<ScheduledWorkResult>> Work { get; set; } = (request, _) => ValueTask.FromResult(Result(request));
    internal int Calls { get; private set; }
    internal IScheduledWorkHandler? HandlerOverride { get; set; }

    internal ScheduledContractFixture()
    {
        Authorization = new(Invocation);
        Registry = new(Authorization);
    }

    internal async Task<ExtensionScope> ActivateAsync(Func<Task>? afterRegistration = null, string[]? capabilities = null)
    {
        var manifest = new ExtensionManifest
        {
            Format = "nexus.extension.manifest", Version = 1, ExtensionId = "fixture", ArtifactName = "Fixture", DisplayName = "Fixture",
            PackageVersion = "0.5.0", Kind = "managed-code", MinHostVersion = "0.18.0", SdkVersion = "0.1.0", ContractFingerprint = SdkIdentity.ContractFingerprint,
            Capabilities = capabilities ?? ["schedule.register", "notification.submit"], Permissions = new(),
            Managed = new() { EntryAssembly = "Fixture.dll", EntryType = "Fixture.Extension" }
        };
        var reader = new ContractReader();
        return await new ExtensionActivator(reader).ActivateManagedAsync(JsonSerializer.SerializeToUtf8Bytes(manifest), "fixture", (admitted, scope) =>
        {
            Scope = scope;
            Schedules = new(admitted, scope, Registry, reader);
            NotificationPort = new(admitted, scope, Authorization, Notifications, reader);
            return new Extension(async cancellation =>
            {
                await Schedules.RegisterAsync("daily", HandlerOverride ?? new Handler((request, token) => { Calls++; return Work(request, token); }), cancellation);
                if (afterRegistration is not null) await afterRegistration();
            });
        }, default);
    }

    internal ValueTask<Core.Scheduling.ScheduledBusinessResult> DispatchAsync() => Registry.DispatchAsync("fixture", "daily", Invocation, default);
    internal ValueTask RegisterRetainedAsync() => Schedules!.RegisterAsync("retained", new Handler(Work), default);

    internal ScheduledInvocation WireInvocation() => new()
    {
        Format = "nexus.schedule.invocation", Version = 1, InstanceId = Invocation.InstanceId.ToString("D"), OccurrenceId = Invocation.OccurrenceId.ToString("D"),
        RunId = Invocation.RunId.ToString("D"), AttemptId = Invocation.AttemptId.ToString("D"), SubjectId = Invocation.SubjectId.ToString("D"),
        NotificationOperationId = Invocation.NotificationOperationId.ToString("D"), DeadlineUtc = Invocation.DeadlineUtc.ToString("O"),
        NotificationTaskIds = Invocation.NotificationTaskIds.Select(id => id.ToString("D")).ToArray()
    };

    internal WireSubmission Submission() => new()
    {
        Format = "nexus.notification.submission", Version = 1, Invocation = WireInvocation(), OperationId = Invocation.NotificationOperationId.ToString("D"),
        NotificationTaskIds = Invocation.NotificationTaskIds.Select(id => id.ToString("D")).ToArray(), Title = "Fixture title", Body = "Fixture body"
    };

    internal static ScheduledWorkResult Result(ScheduledInvocation request) => new()
    {
        Format = "nexus.schedule.work-result", Version = 1, Invocation = request, BusinessOutcome = "notApplicable", ReasonCode = "fixture_work_observed"
    };

    private sealed class Handler(Func<ScheduledInvocation, CancellationToken, ValueTask<ScheduledWorkResult>> callback) : IScheduledWorkHandler
    {
        public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken) => callback(invocation, cancellationToken);
    }

    private sealed class Extension(Func<CancellationToken, Task> activate) : IExtension
    {
        public async ValueTask ActivateAsync(IExtensionScope scope, CancellationToken cancellationToken) => await activate(cancellationToken);
    }

    internal sealed class Policy(FrozenScheduledInvocation approved) : IManualInvocationAuthorization, INotificationSubmissionAuthorization
    {
        internal bool Allowed { get; set; } = true;
        public void AssertAllowed(ManualInvocation invocation, string extensionId, string providerId)
        {
            if (invocation.TaskRevision != 7) throw new UnauthorizedAccessException("fixture_manual_revision");
            AssertAllowed(invocation.Invocation, extensionId, providerId);
        }
        public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId)
        {
            if (!Allowed || !approved.Matches(invocation) || extensionId != "fixture" || providerId != "daily" || invocation.DeadlineUtc <= DateTimeOffset.UtcNow)
                throw new UnauthorizedAccessException("scheduled_invocation_not_authorized");
        }
        public void AssertAllowed(CoreSubmission submission) => AssertAllowed(submission.Invocation, submission.ExtensionId, "daily");
    }

    internal sealed class Queue : INotificationSubmissionQueue
    {
        private readonly Dictionary<Guid, (CoreSubmission Intent, NotificationQueueAcceptance Accepted)> _accepted = [];
        private readonly object _gate = new();
        internal int Calls { get; private set; }
        internal Action? AfterCommit { get; set; }
        internal bool Fail { get; set; }
        internal CoreSubmission? Last { get; private set; }

        public ValueTask<NotificationQueueAcceptance> AcceptAsync(CoreSubmission submission, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Calls++;
                if (Fail) throw new IOException("fixture_queue_failure");
                Last = submission;
                if (_accepted.TryGetValue(submission.OperationId, out var previous))
                {
                    if (!previous.Intent.Invocation.Matches(submission.Invocation) || previous.Intent.Title != submission.Title || previous.Intent.Body != submission.Body ||
                        !previous.Intent.NotificationTaskIds.SequenceEqual(submission.NotificationTaskIds)) throw new IOException("fixture_notification_operation_conflict");
                    return ValueTask.FromResult(previous.Accepted with { IsNew = false });
                }
                var accepted = new NotificationQueueAcceptance(submission.OperationId, Guid.NewGuid(), true);
                _accepted.Add(submission.OperationId, (submission, accepted));
                AfterCommit?.Invoke();
                return ValueTask.FromResult(accepted);
            }
        }
    }
}
