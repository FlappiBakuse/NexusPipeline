using System.Text.Json;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;
using NexusPipeline.Extensibility.State;

namespace Controlled;

internal static class ControlledWorkEffects
{
    internal static async Task ExecuteAsync(ExtensionActivationContext context, string directory, Guid taskId, string mode, CancellationToken cancellation)
    {
        var effects = context.WorkEffects!;
        var activity = mode == "confirm" ? "fixture-new-game" : "fixture-game";
        var revision = 1L;
        var root = await effects.BeginAsync("credential", revision, activity, cancellation);
        var forgedDenied = await Denied(() => effects.SubmittedAsync(Guid.NewGuid(), cancellation));
        WorkEffectLease? role = null;
        bool completeDenied = false, submitDenied = false, repeatDenied = false, confirmedSubmitDenied = false, completedAcquireDenied = false, rotationDenied = false;
        if (root.State is WorkEffectState.New or WorkEffectState.Unknown)
        {
            var roleId = mode is "epoch-b" or "epoch-unknown" ? "other-role" : "fixture-role";
            role = await effects.AcquireAsync(root.Id, roleId, "fixture-server", cancellation);
            completeDenied = await Denied(() => effects.CompleteAsync(root.Id, cancellation));
            if (role.State == WorkEffectState.New)
            {
                await effects.SubmittedAsync(role.Id, cancellation);
                repeatDenied = await Denied(() => effects.SubmittedAsync(role.Id, cancellation));
            }
            else submitDenied = await Denied(() => effects.SubmittedAsync(role.Id, cancellation));
            if (mode == "rotate")
            {
                rotationDenied = await Denied(async () => { _ = await RotateAsync(context, taskId, "different-family|refreshed", cancellation); });
                _ = await RotateAsync(context, taskId, "synthetic-shared-credential|refreshed", cancellation);
                revision = 2;
            }
            if (mode is "recheck" or "confirm" or "rotate" or "epoch-a" or "epoch-a-hold" or "epoch-b")
            {
                await effects.ConfirmedAsync(role.Id, "fixture_query_confirmed", cancellation);
                await effects.CompleteAsync(root.Id, cancellation);
                confirmedSubmitDenied = await Denied(() => effects.SubmittedAsync(role.Id, cancellation));
                completedAcquireDenied = await Denied(async () => { _ = await effects.AcquireAsync(root.Id, "late-role", "server", cancellation); });
            }
        }
        var after = await effects.BeginAsync("credential", revision, activity, cancellation);
        var output = Path.Combine(directory, "effects-" + taskId.ToString("D") + ".json");
        await File.WriteAllBytesAsync(output + ".part", JsonSerializer.SerializeToUtf8Bytes(new
        { game = root.State.ToString(), role = role?.State.ToString(), after = after.State.ToString(), forgedDenied, completeDenied,
            submitDenied, repeatDenied, confirmedSubmitDenied, completedAcquireDenied, rotationDenied }), cancellation);
        File.Move(output + ".part", output, overwrite: true);
        if (mode is "hold" or "epoch-a-hold")
            while (!File.Exists(Path.Combine(directory, "effects-release"))) await Task.Delay(20, cancellation);
    }

    private static ValueTask<DocumentCommit> RotateAsync(ExtensionActivationContext context, Guid taskId, string value, CancellationToken cancellation)
    {
        DocumentSecretEdit[] edits = [new(taskId, "credential", 1, SecretChange.Set(new SecretDraft(value))),
            .. new[] { "one", "two", "three", "four", "five" }.Select(field => new DocumentSecretEdit(taskId, field, 1, SecretChange.Keep()))];
        return context.SecretDocuments!.WriteAsync(Guid.NewGuid(), "tasks", 1, "{}"u8.ToArray(), edits, cancellation);
    }

    private static async Task<bool> Denied(Func<ValueTask> call)
    {
        try { await call(); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
}
