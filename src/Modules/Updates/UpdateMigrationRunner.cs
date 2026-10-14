using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record UpdateRunnerReceipt(string NonceHash, string ImageHash, ProcessIdentity? Identity,
    bool LaunchPending, int Sequence, bool Completed, UpdateInventory Input, UpdateInventory Output)
{
    internal void Validate(UpdateTask task)
    {
        if (!UpdateInventory.Hash(NonceHash) || !UpdateInventory.Hash(ImageHash) || ImageHash != task.TargetImageHash
            || task.TransactionId is null || Sequence is not (0 or 2) || Completed != (Sequence == 2)
            || Completed && (LaunchPending || Identity is null)
            || Identity is { } identity && (identity.Pid <= 0 || identity.StartTime == default
                || !string.Equals(identity.ImageName, Path.Combine(task.StagedDir, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("update.runner_receipt_invalid");
        UpdateMigrationRegistry.Validate(task.ReleaseEvidence!.RouteId, Input, Output);
    }
}

internal sealed record UpdateRunnerFrame(string Kind, string TransactionId, string Nonce, int Protocol, int Sequence,
    string BuildId, string[] Capabilities, string[] MigratorIds, string? RouteId, UpdateInventory? Input, UpdateInventory? Output);

internal static class UpdateMigrationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int FrameLimit = 64 * 1024;
    private const string Argument = "--update-migration-runner";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    internal static bool IsEntry(string[] args) => args.FirstOrDefault() == Argument;

    internal static int RunEntry(string[] args)
    {
        try { RunEntryAsync(args).GetAwaiter().GetResult(); return 0; }
        catch { return 2; }
    }

    private static async Task RunEntryAsync(string[] args)
    {
        if (args.Length != 9 || args[1] != "--pipe" || args[3] != "--transaction" || args[5] != "--nonce" || args[7] != "--parent-pid"
            || args[2] != "nxp-migration-" + args[4] || !Guid.TryParseExact(args[4], "N", out _)
            || !UpdateInventory.Hash(args[6]) || !int.TryParse(args[8], out int parentPid) || parentPid <= 0)
            throw new InvalidDataException("update.runner_arguments");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.GetFullPath(AppContext.BaseDirectory);
        PayloadPathSafety.RequireLinkFree(root);
        var payload = ApplicationPayload.Freeze(root);
        await using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != parentPid)
            throw new InvalidDataException("update.runner_parent_identity");
        var hello = new UpdateRunnerFrame("hello", args[4], args[6], 1, 0, payload.BuildId,
            UpdateTargetManifest.Capabilities, UpdateMigrationRegistry.MigratorIds, null, null, null);
        await WriteFrameAsync(pipe, hello, timeout.Token).ConfigureAwait(false);
        var command = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
        ValidateFrame(command, hello, "execute", 1);
        if (command.Input is null || command.Output is null || command.RouteId is null)
            throw new InvalidDataException("update.runner_input");
        UpdateMigrationRegistry.Validate(command.RouteId, command.Input, command.Output);
        ApplicationPayload.VerifyFrozen(root, payload);
        await WriteFrameAsync(pipe, hello with { Kind = "result", Sequence = 2, RouteId = command.RouteId,
            Input = command.Input, Output = command.Output }, timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<UpdateTask> ExecuteAsync(UpdateTask task, CancellationToken token = default)
    {
        task.Validate();
        if (task.ReleaseEvidence is null) return task;
        if (task.RunnerReceipt is not null)
        {
            RequireStopped(task);
            if (task.RunnerReceipt.Completed) return task;
            throw new IOException("update.runner_incomplete");
        }
        string image = Path.Combine(task.StagedDir, "NexusPipeline.exe");
        ApplicationPayload.VerifyFrozen(task.StagedDir, task.TargetPayload!, task.Version);
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var empty = new UpdateInventory([]);
        var receipt = new UpdateRunnerReceipt(Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(nonce))),
            UpdateApply.ImageHash(image), null, true, 0, false, empty, empty);
        task = task with { RunnerReceipt = receipt };
        task.Write();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeServerStream("nxp-migration-" + task.TransactionId, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
        var start = new ProcessStartInfo(image) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = task.StagedDir };
        foreach (string value in new[] { Argument, "--pipe", "nxp-migration-" + task.TransactionId, "--transaction", task.TransactionId!,
            "--nonce", nonce, "--parent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
        Process? child = null;
        ProcessIdentity? identity = null;
        try
        {
            child = Process.Start(start) ?? throw new IOException("update.runner_start_failed");
            identity = ProcessIdentity.Capture(child) ?? throw new IOException("update.runner_identity_unconfirmed");
            if (!string.Equals(identity.Value.ImageName, image, StringComparison.OrdinalIgnoreCase) || UpdateApply.ImageHash(image) != receipt.ImageHash)
                throw new InvalidDataException("update.runner_image_changed");
            task = task with { RunnerReceipt = receipt with { Identity = identity, LaunchPending = false } };
            task.Write();
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != identity.Value.Pid
                || ProcessIdentity.Capture(child) is not { } actual || !identity.Value.Matches(actual))
                throw new InvalidDataException("update.runner_pipe_identity");
            var expected = new UpdateRunnerFrame("hello", task.TransactionId!, nonce, 1, 0, task.TargetPayload!.BuildId,
                UpdateTargetManifest.Capabilities, UpdateMigrationRegistry.MigratorIds, null, null, null);
            var hello = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            ValidateFrame(hello, expected, "hello", 0);
            if (hello.RouteId is not null || hello.Input is not null || hello.Output is not null)
                throw new InvalidDataException("update.runner_hello_fields");
            var command = expected with { Kind = "execute", Sequence = 1, RouteId = task.ReleaseEvidence.RouteId, Input = empty, Output = empty };
            await WriteFrameAsync(pipe, command, timeout.Token).ConfigureAwait(false);
            var result = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            ValidateFrame(result, expected, "result", 2);
            if (result.RouteId != command.RouteId || result.Input is null || result.Output is null)
                throw new InvalidDataException("update.runner_result_fields");
            UpdateMigrationRegistry.Validate(result.RouteId, result.Input, result.Output);
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (child.ExitCode != 0) throw new IOException("update.runner_exit_failed");
            ApplicationPayload.VerifyFrozen(task.StagedDir, task.TargetPayload, task.Version);
            task = task with { RunnerReceipt = task.RunnerReceipt! with { Sequence = 2, Completed = true, Output = result.Output },
                DataMigration = new(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(task.StagedDir))!, "migration-" + task.TransactionId), [], false) };
            task.Write();
            return task;
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    if (identity is null || ProcessIdentity.Capture(child) is not { } current || !identity.Value.Matches(current)
                        || UpdateApply.ImageHash(image) != receipt.ImageHash) throw new IOException("update.runner_stop_unconfirmed");
                    child.Kill(entireProcessTree: false);
                    if (!child.WaitForExit(5000)) throw new IOException("update.runner_stop_unconfirmed");
                }
                child.Dispose();
            }
        }
    }

    internal static void RequireStopped(UpdateTask task)
    {
        if (task.RunnerReceipt is null) return;
        string image = Path.Combine(task.StagedDir, "NexusPipeline.exe");
        if (!File.Exists(image) && task.RunnerReceipt is { Completed: true, Identity: { } identity })
        {
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                if (process.HasExited || process.StartTime.ToUniversalTime() != identity.StartTime) return;
            }
            catch (ArgumentException) { return; }
            throw new IOException("update.runner_stop_unconfirmed");
        }
        if (RuntimeImageActivityProbe.ObserveExecutable(image) != "inactive")
            throw new IOException("update.runner_stop_unconfirmed");
    }

    internal static void ValidateFrame(UpdateRunnerFrame frame, UpdateRunnerFrame expected, string kind, int sequence)
    {
        if (frame.Kind != kind || frame.TransactionId != expected.TransactionId || frame.Nonce != expected.Nonce
            || frame.Protocol != 1 || frame.Sequence != sequence || frame.BuildId != expected.BuildId
            || frame.Capabilities is null || frame.MigratorIds is null
            || !frame.Capabilities.Order(StringComparer.Ordinal).SequenceEqual(expected.Capabilities.Order(StringComparer.Ordinal))
            || !frame.MigratorIds.Order(StringComparer.Ordinal).SequenceEqual(expected.MigratorIds.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("update.runner_handshake");
    }

    internal static async Task<UpdateRunnerFrame> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > FrameLimit) throw new InvalidDataException("update.runner_frame_size");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var node = UpdateJson.Parse(bytes);
        string[] fields = ["Kind", "TransactionId", "Nonce", "Protocol", "Sequence", "BuildId", "Capabilities", "MigratorIds", "RouteId", "Input", "Output"];
        if (node is not System.Text.Json.Nodes.JsonObject obj || obj.Count != fields.Length || fields.Any(field => !obj.ContainsKey(field)))
            throw new InvalidDataException("update.runner_frame_fields");
        return JsonSerializer.Deserialize<UpdateRunnerFrame>(bytes, JsonOptions) ?? throw new InvalidDataException("update.runner_frame_null");
    }

    internal static async Task WriteFrameAsync(Stream stream, UpdateRunnerFrame frame, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (bytes.Length > FrameLimit) throw new InvalidDataException("update.runner_frame_size");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
