using System.Text.Json;
using NexusPipeline.Extensibility.Frontend;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;

namespace Controlled;

internal sealed class ControlledManualWork(ExtensionActivationContext context, string directory) : IManualWorkHandler
{
    private sealed record Input(Guid TaskId, long Revision, bool Hold, Guid? PreviousRunId = null, Guid? PreviousAttemptId = null,
        string? Effects = null, Guid[]? NotificationIds = null, bool RetryNotification = false);
    private Input Read() => JsonSerializer.Deserialize<Input>(File.ReadAllBytes(Path.Combine(directory, "manual-task.json")))!;
    public string IdentityVersion => "fixture-v1";
    public SecretDraft ProjectIdentity(string field, SecretDraft material)
    {
        try { _ = context.WorkTasks!.ReadClockAsync(default).GetAwaiter().GetResult(); throw new IOException("fixture_projection_ports_allowed"); }
        catch (UnauthorizedAccessException)
        { File.WriteAllText(Path.Combine(directory, "projection-ports-denied"), "denied"); }
        SecretDraft? result = null;
        material.Use(value => { var split = value.IndexOf('|'); result = new(split < 0 ? value : value[..split]); }); return result!;
    }
    public bool CanRotateIdentity(string field, SecretDraft before, SecretDraft after)
    {
        var accepted = false;
        before.Use(first =>
        {
            var original = first.ToString();
            after.Use(second => accepted = field == "credential" && original.StartsWith("synthetic-", StringComparison.Ordinal) &&
                second.SequenceEqual(original + "|refreshed"));
        });
        return accepted;
    }

    public ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(directory, "manual-task.json"))) return ValueTask.FromResult<ManualWorkTask?>(null);
        var saved = Read();
        return ValueTask.FromResult<ManualWorkTask?>(taskId != saved.TaskId ? null : new ManualWorkTask
        {
            Format = "nexus.manual.work-task", Version = 1, TaskId = taskId.ToString("D"), Revision = saved.Revision,
            IsEnabled = true, TimeZoneId = "UTC", TimeoutSeconds = 60,
            NotificationTaskIds = (saved.NotificationIds ?? []).Select(id => id.ToString("D")).ToList(), ScheduledTaskIds = []
        });
    }

    public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken) =>
        throw new IOException("fixture_manual_context_required");

    public async ValueTask<ScheduledWorkResult> ExecuteManualAsync(ManualWorkInvocation invocation, CancellationToken cancellationToken)
    {
        var saved = Read();
        if (saved.Revision != invocation.TaskRevision) throw new IOException("fixture_private_revision_changed");
        var current = await context.WorkTasks!.ReadCurrentAsync(cancellationToken);
        if (await context.WorkTasks.HasStoppedAsync("manual", saved.TaskId, current.RunId, current.AttemptId, cancellationToken))
            throw new IOException("fixture_live_writer_marked_stopped");
        var foreignDenied = false;
        try { _ = await context.WorkTasks.HasStoppedAsync("foreign", saved.TaskId, current.RunId, current.AttemptId, cancellationToken); }
        catch (UnauthorizedAccessException) { foreignDenied = true; }
        if (!foreignDenied) throw new IOException("fixture_foreign_provider_allowed");
        if (saved.PreviousRunId is { } previous && saved.PreviousAttemptId is { } attempt)
        {
            var stopped = await context.WorkTasks.HasStoppedAsync("manual", saved.TaskId, previous, attempt, cancellationToken);
            var unknown = await context.WorkTasks.HasStoppedAsync("manual", saved.TaskId, Guid.NewGuid(), Guid.NewGuid(), cancellationToken);
            File.WriteAllBytes(Path.Combine(directory, "writer-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new { stopped, unknown }));
        }
        var entered = Path.Combine(directory, "manual-entered.json");
        await File.WriteAllBytesAsync(entered + ".part", JsonSerializer.SerializeToUtf8Bytes(current), cancellationToken);
        File.Move(entered + ".part", entered, overwrite: true);
        try
        {
            if (saved.NotificationIds is { } notificationIds)
                await ControlledManualNotifications.ExecuteAsync(context, directory, invocation, notificationIds, saved.RetryNotification, cancellationToken);
            if (saved.Effects is { } effects)
                await ControlledWorkEffects.ExecuteAsync(context, directory, saved.TaskId, effects, cancellationToken);
            if (saved.Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new() { Format = "nexus.schedule.work-result", Version = 1, Invocation = invocation.Invocation,
                BusinessOutcome = "notApplicable", ReasonCode = "fixture_manual_complete" };
        }
        finally { File.WriteAllText(Path.Combine(directory, "manual-stopped"), "stopped"); }
    }

    internal static async ValueTask<FrontendResult> InvokeAsync(ExtensionActivationContext context, FrontendCall call,
        string directory, CancellationToken cancellation)
    {
        if (call.Operation == "manual-notify")
            return await ControlledManualNotifications.InvokeAsync(context, directory, cancellation);
        if (call.Operation == "manual-current") return Json(await context.WorkTasks!.ReadCurrentAsync(cancellation));
        if (call.Operation == "manual-clock") return Json(await context.WorkTasks!.ReadClockAsync(cancellation));
        using var document = JsonDocument.Parse(call.Payload);
        var taskId = document.RootElement.GetProperty("taskId").GetGuid();
        var revision = document.RootElement.GetProperty("revision").GetInt64();
        if (call.Operation == "manual-state") return Json(await context.WorkTasks!.ReadAsync("manual", [taskId], cancellation));
        if (call.Operation == "manual-run") return Json(await context.WorkTasks!.RunAsync("manual", taskId, revision, cancellation));
        var hold = await context.WorkTasks!.QuiesceAsync("manual", taskId, revision, cancellation);
        if (call.Operation == "manual-leak") return Json(new { accepted = true });
        await using (hold)
        {
            File.WriteAllText(Path.Combine(directory, "quiesce-entered"), "entered");
            var deadline = Environment.TickCount64 + 15000;
            while (!File.Exists(Path.Combine(directory, "quiesce-release")) && Environment.TickCount64 < deadline)
                await Task.Delay(20, cancellation);
            if (!File.Exists(Path.Combine(directory, "quiesce-release"))) throw new IOException("fixture_quiescence_timeout");
            return Json(new { accepted = true });
        }
    }

    private static FrontendResult Json<T>(T value) => new("application/json", JsonSerializer.SerializeToUtf8Bytes(value));
}
