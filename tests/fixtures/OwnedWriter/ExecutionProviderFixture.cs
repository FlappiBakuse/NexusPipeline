using System.IO.Pipes;
using System.Text.Json;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;

internal static class ExecutionProviderFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var mode, var marker]) return 94;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if (mode.StartsWith("wrong-", StringComparison.Ordinal) || mode == "oversized")
            return await SendMalformedAsync(mode, deadline.Token);
        await using var channel = await ExecutionProviderChannel.ConnectAsync(Console.OpenStandardInput(), deadline.Token);
        var staging = marker + ".prepared";
        await File.WriteAllTextAsync(staging, Environment.ProcessId + "\\n" + Environment.CurrentDirectory, deadline.Token);
        File.Move(staging, marker, overwrite: true);
        await Console.Out.WriteLineAsync("provider-native-stdout");
        await Console.Out.FlushAsync();
        await channel.PublishProgressAsync("actual-worker-running", deadline.Token);
        if (mode.StartsWith("device-", StringComparison.Ordinal))
        {
            try { await DeviceReaderFixture.RunAsync(channel, marker, mode, deadline.Token); }
            catch (Exception error)
            {
                await File.WriteAllTextAsync(marker + ".device-error", error.ToString(), CancellationToken.None);
                throw;
            }
            await channel.PublishTerminalAsync(mode == "device-cancel" ? "cancelled" : "succeeded", "device_fixture_completed", deadline.Token);
            return 0;
        }
        if (mode is "graceful-stop" or "unadvertised-stop")
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, channel.Stopping); }
            catch (OperationCanceledException) when (channel.Stopping.IsCancellationRequested) { }
            await File.WriteAllTextAsync(marker + ".drained", "native-writers-drained", deadline.Token);
            await channel.PublishTerminalAsync("cancelled", "provider_fixture_stopped", deadline.Token);
            return 0;
        }
        if (mode == "preview-repeat")
        {
            if (File.Exists(marker + ".failed")) await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            while (!File.Exists(marker + ".fail")) await Task.Delay(20, deadline.Token);
            await File.WriteAllTextAsync(marker + ".failed", "first invocation failed", deadline.Token);
            await channel.PublishTerminalAsync("failed", "provider_fixture_failed", deadline.Token);
            return 0;
        }
        if (mode == "project-task-retry")
        {
            await TaskRetryFixture.RunAsync(channel, marker, deadline.Token);
            return 0;
        }
        if (mode == "project-read") await ProjectReaderFixture.ReadAsync(channel, marker, deadline.Token);
        if (mode.StartsWith("project-secret", StringComparison.Ordinal)) await ProjectSecretFixture.ReadAsync(channel, marker, mode, deadline.Token);
        if (mode is "project-peer" or "project-fingerprint" or "project-sequence")
            await ProjectDenialFixture.VerifyAsync(channel, marker, mode, deadline.Token);
        if (mode is "retry-failed" or "retry-interrupted" or "retry-config-failed" or "retry-config-interrupted" or "retry-channel-loss" or "retry-second-hold" or "ordinary-retry" or "ordinary-config-retry" or "retry-invocation-cap")
        {
            var bootstrap = channel.Bootstrap;
            var sink = marker + ".effects"; Directory.CreateDirectory(sink);
            var key = Path.Combine(sink, bootstrap.IdempotencyKey);
            try
            {
                using var effect = new FileStream(key, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var bytes = System.Text.Encoding.UTF8.GetBytes(bootstrap.IdempotencyKey);
                await effect.WriteAsync(bytes, deadline.Token); effect.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(key)) { }
            await File.AppendAllTextAsync(marker + ".attempts", bootstrap.Identity.AttemptId + "|" + bootstrap.AttemptOrdinal +
                "|" + bootstrap.IdempotencyKey + Environment.NewLine, deadline.Token);
            var physicalOrdinal = File.ReadAllLines(marker + ".attempts").Length;
            if (mode is "retry-config-failed" or "retry-config-interrupted" or "retry-channel-loss" or "ordinary-config-retry")
            {
                var target = bootstrap.ConfigurationTargets.Single();
                if (await File.ReadAllTextAsync(target, deadline.Token) != "{\"fixture\":true}")
                    throw new IOException("provider_fixture_retry_candidate_replaced");
                await File.WriteAllTextAsync(target, "{\"fixture\":true,\"providerChanged\":true,\"count\":" +
                    (40 + (mode == "ordinary-config-retry" ? physicalOrdinal : bootstrap.AttemptOrdinal)) + "}", deadline.Token);
                await File.WriteAllTextAsync(marker + ".configuration-" + bootstrap.AttemptOrdinal, "written", deadline.Token);
            }
            if (mode == "retry-invocation-cap" && physicalOrdinal == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            if (bootstrap.AttemptOrdinal == 1 && mode == "retry-channel-loss") return 37;
            if (bootstrap.AttemptOrdinal == 1 && (mode is "retry-interrupted" or "retry-config-interrupted" or "retry-second-hold"))
                await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            if (bootstrap.AttemptOrdinal > 1 && mode == "retry-second-hold")
                await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
            var failed = mode == "retry-invocation-cap" || (mode is "ordinary-retry" or "ordinary-config-retry" ? physicalOrdinal == 1 : bootstrap.AttemptOrdinal == 1);
            await channel.PublishTerminalAsync(failed ? "failed" : "succeeded",
                failed ? "provider_fixture_failed" : "provider_fixture_succeeded", deadline.Token);
            return 0;
        }
        if (mode == "progress-limit")
        {
            try
            {
                for (var index = 0; index < 10002; index++)
                    await channel.PublishProgressAsync(new string('p', 256), deadline.Token);
                throw new IOException("provider_fixture_budget_not_reached");
            }
            catch (IOException error) when (error.Message is "execution_provider_transcript_limit" or "execution_provider_channel_closed")
            {
                await File.WriteAllTextAsync(marker + ".budget-hit", error.Message, deadline.Token);
            }
        }
        if (mode is "configuration-launcher" or "terminal-after-launcher")
            while (!File.Exists(marker + ".launcher")) await Task.Delay(20, deadline.Token);
        if (mode is "configuration" or "configuration-wait" or "configuration-launcher")
        {
            var target = channel.Bootstrap.ConfigurationTargets.Single();
            if (await File.ReadAllTextAsync(target, deadline.Token) != "{\"fixture\":true}")
                throw new IOException("provider_fixture_not_installed");
            await File.WriteAllTextAsync(target, "{\"fixture\":true,\"providerChanged\":true,\"count\":41}", deadline.Token);
        }
        if (mode is "wait" or "configuration-wait") { await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token); return 95; }
        if (mode == "no-terminal") return 0;
        if (mode == "terminal-gated")
            while (!File.Exists(marker + ".terminal-release")) await Task.Delay(20, deadline.Token);
        await channel.PublishTerminalAsync(mode == "terminal-failed" ? "failed" : "succeeded",
            mode == "terminal-failed" ? "provider_fixture_failed" : "provider_fixture_succeeded", deadline.Token);
        if (mode == "terminal-gated")
        {
            await File.WriteAllTextAsync(marker + ".terminal-sent", "sent", deadline.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        }
        return mode == "native-failed" ? 37 : 0;
    }
    private static async Task<int> SendMalformedAsync(string mode, CancellationToken cancellation)
    {
        var bootstrap = JsonSerializer.Deserialize<ExecutionProviderBootstrap>(
            await ExecutionProviderFrames.ReadAsync(Console.OpenStandardInput(), ExecutionProviderFrames.MaximumBootstrapBytes, cancellation))!;
        await using var pipe = new NamedPipeClientStream(".", bootstrap.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellation);
        _ = await ExecutionProviderFrames.ReadAsync(pipe, 1, cancellation);
        if (mode == "oversized")
        {
            var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, ExecutionProviderFrames.MaximumEventBytes + 1);
            await pipe.WriteAsync(length, cancellation); await pipe.FlushAsync(cancellation); return 0;
        }
        var identity = bootstrap.Identity with
        {
            AttemptId = mode == "wrong-attempt" ? Guid.NewGuid().ToString("D") : bootstrap.Identity.AttemptId,
            Nonce = mode == "wrong-nonce" ? new string('e', 64) : bootstrap.Identity.Nonce
        };
        var frame = new ExecutionProviderEvent { Format = "nexus.execution.provider-event", Version = 1, Identity = identity,
            Kind = "started", Sequence = mode == "wrong-sequence" ? 2 : 1 };
        await ExecutionProviderFrames.WriteAsync(pipe, ExecutionProviderFrames.Encode(frame, ExecutionProviderFrames.MaximumEventBytes),
            ExecutionProviderFrames.MaximumEventBytes, cancellation);
        return 0;
    }
}
