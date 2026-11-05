using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;

internal static class ProjectDenialFixture
{
    internal static async Task VerifyAsync(ExecutionProviderChannel channel, string marker, string mode, CancellationToken cancellation)
    {
        if (mode == "project-peer")
        {
            var path = marker + ".bootstrap";
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(channel.Bootstrap), cancellation);
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "project-foreign-reader", path, marker }
            }) ?? throw new IOException("project_fixture_child_missing");
            await child.WaitForExitAsync(cancellation);
            if (child.ExitCode != 0) throw new IOException("project_fixture_foreign_admitted");
            return;
        }
        var bootstrap = channel.Bootstrap;
        await using var pipe = new NamedPipeClientStream(".", bootstrap.Project!.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellation);
        var admitted = await ExecutionProviderFrames.ReadAsync(pipe, 1, cancellation);
        if (admitted is not [1]) throw new IOException("project_fixture_root_denied");
        var request = new ExecutionProjectRequest
        {
            Fingerprint = mode == "project-fingerprint" ? new string('0', 64) : bootstrap.Project.Fingerprint,
            Sequence = mode == "project-sequence" ? 2 : 1, Kind = "manifest"
        };
        await ExecutionProviderFrames.WriteAsync(pipe, ExecutionProviderFrames.Encode(request, 65536), 65536, cancellation);
        try
        {
            _ = await ExecutionProviderFrames.ReadAsync(pipe, ExecutionProviderFrames.MaximumBootstrapBytes, cancellation);
            throw new InvalidOperationException("project_fixture_frame_admitted");
        }
        catch (IOException)
        {
            await RecordAsync(marker, bootstrap.Project.Fingerprint, cancellation);
        }
    }

    internal static async Task<int> ForeignAsync(string[] arguments)
    {
        if (arguments is not [var path, var marker]) return 94;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var bootstrap = JsonSerializer.Deserialize<ExecutionProviderBootstrap>(await File.ReadAllBytesAsync(path, deadline.Token))!;
        try
        {
            await using var reader = await ExecutionProjectReader.ConnectAsync(bootstrap, deadline.Token);
            _ = await reader.ManifestAsync(deadline.Token); return 95;
        }
        catch (IOException)
        { await RecordAsync(marker, bootstrap.Project!.Fingerprint, deadline.Token); return 0; }
    }

    private static Task RecordAsync(string marker, string fingerprint, CancellationToken cancellation) =>
        File.WriteAllTextAsync(marker + ".project", JsonSerializer.Serialize(new
            { Fingerprint = fingerprint, denied = true, processId = Environment.ProcessId }), cancellation);
}
