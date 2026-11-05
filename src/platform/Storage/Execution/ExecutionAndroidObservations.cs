using System.Text.Json;
using System.Text.RegularExpressions;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal enum ExecutionAndroidState
{
    Unknown, Connected, Online, Offline, Ready, NotReady, LaunchRequested, ApplicationStartRequested,
    ApplicationStopRequested, ShutdownRequested, Foreground, Failed, Booting
}
internal sealed record ExecutionAndroidObservation(BlobReference Artifact, ExecutionAndroidAction Action,
    ExecutionAndroidState State, string? ForegroundPackage)
{
    internal TargetCommandObservation Interpretation { get; init; }
}

internal static class ExecutionAndroidObservations
{
    internal static ExecutionAndroidObservation ReadSaved(AndroidControlEvidence reader, AndroidCommandRequest request,
        AndroidCommandObservation native, BlobReference artifact)
    {
        if (native.Binding != request.Binding || native.RequestDigest != reader.Digest(request))
            throw new IOException("execution_android_observation_unbound");
        if (native.Completion == AndroidResponseCompletion.OwnedLaunchHandoff && request.OwnedHandoff)
            return new(artifact, request.Action, ExecutionAndroidState.LaunchRequested, null) { Interpretation = TargetCommandObservation.OwnedHandoff };
        if (native.Completion != AndroidResponseCompletion.NaturalRootExitAndEofBeforeStop ||
            !native.StartReturned || native.Root is null || native.ExitCode is null || !native.OutputComplete)
            return Unknown(artifact, request.Action, native.OutputComplete ? TargetCommandObservation.Unverified :
                TargetCommandObservation.Incomplete);
        (string StandardOutput, string StandardError) text;
        try { text = reader.ReadText(native); }
        catch (System.Text.DecoderFallbackException) { return Unknown(artifact, request.Action); }
        return Interpret(artifact, request.Action, request.Driver == "mumu" ? ExecutionAndroidDriver.MuMu : ExecutionAndroidDriver.GenericAdb,
            request.Mapping?.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Mapping is null ? null : checked((int)request.Mapping.EndpointPort), request.Endpoint, native.ExitCode.Value,
            text.StandardOutput, text.StandardError);
    }

    internal static int ConsecutiveOffline(int previous, ExecutionAndroidObservation observation)
    {
        if (previous is < 0 or > 2 || observation.Action != ExecutionAndroidAction.OfflineProbe || !Enum.IsDefined(observation.State))
            throw new ArgumentException("execution_android_offline_observation_invalid");
        return observation.State == ExecutionAndroidState.Offline &&
            observation.Interpretation == TargetCommandObservation.ExpectedOffline ? Math.Min(2, previous + 1) : 0;
    }

    internal static ExecutionAndroidObservation Interpret(BlobReference artifact, ExecutionAndroidAction action, ExecutionAndroidDriver driver,
        string? index, int? port, string endpoint, int exitCode, string stdout, string stderr)
    {
        var combined = stdout + "\n" + stderr;
        if (action == ExecutionAndroidAction.OfflineProbe)
        {
            if (driver == ExecutionAndroidDriver.MuMu)
            {
                if (exitCode != 0 || !string.IsNullOrWhiteSpace(stderr)) return Unknown(artifact, action);
                var saved = MuMuState(stdout, index!, port!.Value);
                if (saved is { Process: false, Android: null or false }) return Offline(artifact, action);
                return saved is { Process: true, Android: null or true or false }
                    ? Complete(artifact, action, ExecutionAndroidState.Online) : Unknown(artifact, action);
            }
            if (exitCode != 0 && ExplicitAdbOffline(stdout, stderr, endpoint)) return Offline(artifact, action);
            if (exitCode != 0 && HasOfflineLine(combined, endpoint))
                return Unknown(artifact, action, TargetCommandObservation.Contradictory);
            return exitCode == 0 && stdout.Trim() == "ok" && string.IsNullOrWhiteSpace(stderr)
                ? Complete(artifact, action, ExecutionAndroidState.Online) : Unknown(artifact, action);
        }
        if (action == ExecutionAndroidAction.DeviceState && driver == ExecutionAndroidDriver.GenericAdb &&
            exitCode != 0 && ExplicitAdbOffline(stdout, stderr, endpoint))
            return new(artifact, action, ExecutionAndroidState.NotReady, null)
            { Interpretation = TargetCommandObservation.ExpectedOffline };
        if ((action is ExecutionAndroidAction.DeviceState or ExecutionAndroidAction.Foreground) &&
            (exitCode != 0 || !string.IsNullOrWhiteSpace(stderr)))
            return Unknown(artifact, action, HasOfflineLine(combined, endpoint) && !ExplicitAdbOffline(stdout, stderr, endpoint)
                ? TargetCommandObservation.Contradictory : TargetCommandObservation.Unverified);
        if (exitCode != 0 || action == ExecutionAndroidAction.Connect && ConnectFailed(combined) ||
            (action is ExecutionAndroidAction.StartApplication or ExecutionAndroidAction.StopApplication or ExecutionAndroidAction.Shutdown or
                ExecutionAndroidAction.LaunchDevice) && ExplicitEffectFailure(combined))
            return Complete(artifact, action, ExecutionAndroidState.Failed);
        if (action == ExecutionAndroidAction.Foreground)
        {
            var packages = Regex.Matches(stdout, @"(?:mCurrentFocus|mFocusedApp)[^\r\n]*?\b([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)/",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
            return packages.Length switch
            {
                1 => new(artifact, action, ExecutionAndroidState.Foreground, packages[0]) { Interpretation = TargetCommandObservation.Complete },
                > 1 => Unknown(artifact, action, TargetCommandObservation.Contradictory),
                _ => Unknown(artifact, action)
            };
        }
        if (action == ExecutionAndroidAction.DeviceState)
        {
            if (driver == ExecutionAndroidDriver.GenericAdb) return stdout.Trim() switch
            {
                "device" => Complete(artifact, action, ExecutionAndroidState.Ready),
                "offline" => Complete(artifact, action, ExecutionAndroidState.NotReady),
                _ => Unknown(artifact, action)
            };
            var state = MuMuState(stdout, index!, port!.Value);
            return state switch
            {
                { Process: true, Android: true } => Complete(artifact, action, ExecutionAndroidState.Ready),
                { Process: false, Android: null or false } => Complete(artifact, action, ExecutionAndroidState.NotReady),
                { Process: true, Android: false } => Complete(artifact, action, ExecutionAndroidState.Booting),
                _ => Unknown(artifact, action)
            };
        }
        return Complete(artifact, action, action switch
        {
            ExecutionAndroidAction.Connect => ExecutionAndroidState.Connected,
            ExecutionAndroidAction.LaunchDevice => ExecutionAndroidState.LaunchRequested,
            ExecutionAndroidAction.StartApplication => ExecutionAndroidState.ApplicationStartRequested,
            ExecutionAndroidAction.StopApplication => ExecutionAndroidState.ApplicationStopRequested,
            ExecutionAndroidAction.Shutdown => ExecutionAndroidState.ShutdownRequested,
            _ => ExecutionAndroidState.Unknown
        });
    }

    private static (bool? Process, bool? Android)? MuMuState(string text, string index, int port)
    {
        if (!ObjectCandidate(text)) return null;
        try
        {
            using var parsed = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(text));
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty(index, out var child)) root = child;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("adb_port", out var actualPort) ||
                !actualPort.TryGetInt32(out var number) || number != port ||
                !root.TryGetProperty("is_process_started", out var process) || process.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            bool? android = null;
            if (root.TryGetProperty("is_android_started", out var value))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                android = value.GetBoolean();
            }
            return (process.GetBoolean(), android);
        }
        catch (JsonException) { return null; }
    }

    private static ExecutionAndroidObservation Offline(BlobReference artifact, ExecutionAndroidAction action) =>
        new(artifact, action, ExecutionAndroidState.Offline, null) { Interpretation = TargetCommandObservation.ExpectedOffline };
    private static ExecutionAndroidObservation Complete(BlobReference artifact, ExecutionAndroidAction action, ExecutionAndroidState state) =>
        new(artifact, action, state, null) { Interpretation = TargetCommandObservation.Complete };
    private static ExecutionAndroidObservation Unknown(BlobReference artifact, ExecutionAndroidAction action,
        TargetCommandObservation interpretation = TargetCommandObservation.Unverified) =>
        new(artifact, action, ExecutionAndroidState.Unknown, null) { Interpretation = interpretation };
    private static bool ExplicitAdbOffline(string stdout, string stderr, string endpoint)
    {
        var lines = Lines(stdout + "\n" + stderr);
        return lines.Length > 0 && lines.All(line => IsOfflineLine(line, endpoint));
    }

    private static bool HasOfflineLine(string text, string endpoint) => Lines(text).Any(line => IsOfflineLine(line, endpoint));

    private static bool IsOfflineLine(string line, string endpoint)
    {
        foreach (var prefix in new[] { "adb: error: ", "adb: ", "error: ", "" })
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[prefix.Length..];
            if (value.Equals("device offline", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no devices/emulators found", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("device '" + endpoint + "' not found", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("device \"" + endpoint + "\" not found", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ExplicitEffectFailure(string text)
    {
        if (Lines(text).Any(line => Regex.IsMatch(line, @"^(?:error(?:\s+type\s+\d+)?\s*:|failure(?:\s*\[|\s*:)|failed(?:\s+to\b|\s*:)|exception\s+occurred\s+while\s+executing\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))) return true;
        if (!ObjectCandidate(text)) return false;
        try
        {
            using var document = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(text));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in new[] { "code", "error_code", "errorCode" })
                if (document.RootElement.TryGetProperty(name, out var code) && code.TryGetInt64(out var number) && number != 0) return true;
            if (document.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return true;
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString());
        }
        catch (JsonException) { return false; }
    }

    private static bool ObjectCandidate(string text)
    {
        foreach (var character in text)
        {
            if (character is ' ' or '\t' or '\r' or '\n') continue;
            return character == '{';
        }
        return false;
    }

    private static bool ConnectFailed(string text) => new[] { "cannot connect", "failed to connect", "unable to connect" }
        .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
