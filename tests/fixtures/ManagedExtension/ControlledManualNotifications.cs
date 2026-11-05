using System.Text.Json;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;

namespace Controlled;

internal static class ControlledManualNotifications
{
    internal static async Task ExecuteAsync(ExtensionActivationContext context, string directory, ManualWorkInvocation invocation,
        IReadOnlyList<Guid> ids, bool retry, CancellationToken cancellation)
    {
        var submission = new NotificationSubmission { Format = "nexus.notification.submission", Version = 1,
            Invocation = invocation.Invocation, OperationId = invocation.Invocation.NotificationOperationId,
            NotificationTaskIds = ids.Select(id => id.ToString("D")).ToList(), Title = "Synthetic manual work", Body = "Synthetic complete result" };
        var forgedOperation = await Rejected(async () => { _ = await context.Notifications.SubmitAsync(submission with { OperationId = Guid.NewGuid().ToString("D") }, cancellation); });
        var forgedTarget = await Rejected(async () => { _ = await context.Notifications.SubmitAsync(submission with { NotificationTaskIds = [Guid.NewGuid().ToString("D")] }, cancellation); });
        var forgedRun = await Rejected(async () => { _ = await context.Notifications.SubmitAsync(submission with {
            Invocation = invocation.Invocation with { RunId = Guid.NewGuid().ToString("D") } }, cancellation); });
        if (retry)
        {
            var failed = await Rejected(async () => { _ = await context.Notifications.SubmitAsync(submission, cancellation); });
            if (!failed) throw new IOException("fixture_notification_sql_failure_missing");
            File.WriteAllText(Path.Combine(directory, "manual-notification-failed"), "failed");
            await WaitAsync(directory, "manual-notification-retry", cancellation);
        }
        var accepted = await context.Notifications.SubmitAsync(submission, cancellation);
        var replay = await context.Notifications.SubmitAsync(submission, cancellation);
        var changedBody = await Rejected(async () => { _ = await context.Notifications.SubmitAsync(submission with { Body = "changed body" }, cancellation); });
        await File.WriteAllBytesAsync(Path.Combine(directory, "manual-notification-input.json"), JsonSerializer.SerializeToUtf8Bytes(submission), cancellation);
        await File.WriteAllBytesAsync(Path.Combine(directory, "manual-notification.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { accepted, replay, forgedOperation, forgedTarget, forgedRun, changedBody }), cancellation);
        await WaitAsync(directory, "manual-notification-release", cancellation);
    }

    internal static async ValueTask<FrontendResult> InvokeAsync(ExtensionActivationContext context, string directory, CancellationToken cancellation)
    {
        var submission = JsonSerializer.Deserialize<NotificationSubmission>(File.ReadAllBytes(Path.Combine(directory, "manual-notification-input.json")))!;
        return new("application/json", JsonSerializer.SerializeToUtf8Bytes(await context.Notifications.SubmitAsync(submission, cancellation)));
    }

    private static async Task<bool> Rejected(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception error) when (error is not OperationCanceledException) { return true; }
    }
    private static async Task WaitAsync(string directory, string name, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(directory, name))) await Task.Delay(20, deadline.Token);
    }
}
