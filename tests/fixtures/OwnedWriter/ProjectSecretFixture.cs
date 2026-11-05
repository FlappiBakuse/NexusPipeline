using System.Text.Json;
using NexusPipeline.Extensibility.Execution;

internal static class ProjectSecretFixture
{
    internal static async Task ReadAsync(ExecutionProviderChannel channel, string marker, string mode, CancellationToken cancellation)
    {
        await using var reader = await ExecutionProjectReader.ConnectAsync(channel.Bootstrap, cancellation);
        var manifest = await reader.ManifestAsync(cancellation);
        var selected = manifest.Selection ?? throw new IOException("project_secret_selection_missing");
        if (!selected.SelectedTaskIds.Contains("fixture:taskA", StringComparer.Ordinal)) throw new IOException("project_secret_task_missing");
        if (JsonSerializer.Serialize(manifest).Contains("PROTECTED_PIPE_FIXTURE", StringComparison.Ordinal))
            throw new IOException("project_secret_manifest_leaked");
        var verified = false; var disposed = false; var denied = false;
        if (mode == "project-secret-revision")
        {
            await File.WriteAllTextAsync(marker + ".secret-ready", "ready", cancellation);
            while (!File.Exists(marker + ".secret-release")) await Task.Delay(10, cancellation);
        }
        else
        {
            var secret = await reader.ReadSecretAsync("input-fixture", "fixture:taskA", cancellation);
            secret.Use(value => verified = value.SequenceEqual("PROTECTED_PIPE_FIXTURE_密码🔒"));
            secret.Dispose();
            try { secret.Use(_ => { }); } catch (ObjectDisposedException) { disposed = true; }
            if (!verified || !disposed) throw new IOException("project_secret_owner_invalid");
        }
        if (mode != "project-secret")
        {
            try
            {
                using var forbidden = await reader.ReadSecretAsync(mode == "project-secret-field" ? "input-undeclared" : "input-fixture",
                    mode == "project-secret-task" ? "fixture:not-selected" : "fixture:taskA", cancellation);
            }
            catch (IOException) { denied = true; }
            if (!denied) throw new IOException("project_secret_denial_missing");
        }
        foreach (var task in selected.SelectedTaskIds) await Console.Out.WriteLineAsync(task + ":succeeded");
        await Console.Out.FlushAsync(cancellation);
        await File.WriteAllTextAsync(marker + ".project", JsonSerializer.Serialize(new { manifest.Fingerprint, processId = Environment.ProcessId,
            verified, disposed, denied, selection = manifest.Selection }), cancellation);
    }
}
