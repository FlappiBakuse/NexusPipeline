using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;

namespace NexusPipeline.Platform.Storage.Execution;

internal enum ExecutionAndroidDriver { GenericAdb, MuMu }
internal enum ExecutionAndroidAction { Connect, LaunchDevice, DeviceState, StartApplication, Foreground, StopApplication, Shutdown, OfflineProbe, Screenshot }
internal sealed record ExecutionAndroidCommand(ExecutionAndroidAction Action, ImmutableArray<string> Arguments);

internal static class ExecutionAndroidCommands
{
    internal static string Endpoint(string value)
    {
        if (value is null || value.Length is < 3 or > 512 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            !Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is < 1 or > 65535 ||
            value.LastIndexOf(':') < 1 || !int.TryParse(value[(value.LastIndexOf(':') + 1)..], out var port) || port != uri.Port)
            throw new ArgumentException("execution_android_endpoint_invalid");
        var host = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) ? address.ToString() : uri.IdnHost.ToLowerInvariant();
        return host.Contains(':') ? "[" + host + "]:" + port : host + ":" + port;
    }

    internal static ExecutionAndroidCommand Generic(string endpoint, ExecutionAndroidAction action, IReadOnlyList<string> startArguments,
        string package)
    {
        var normalized = Endpoint(endpoint); Require(action, startArguments, package);
        ImmutableArray<string> arguments = action switch
        {
            ExecutionAndroidAction.Connect => ImmutableArray.Create("connect", normalized),
            ExecutionAndroidAction.StartApplication => ImmutableArray.Create("-s", normalized, "shell", "am", "start").AddRange(startArguments),
            ExecutionAndroidAction.DeviceState => ["-s", normalized, "get-state"],
            ExecutionAndroidAction.Foreground => ["-s", normalized, "shell", "dumpsys", "window"],
            ExecutionAndroidAction.StopApplication => ["-s", normalized, "shell", "am", "force-stop", package],
            ExecutionAndroidAction.Shutdown => ["-s", normalized, "shell", "reboot", "-p"],
            ExecutionAndroidAction.OfflineProbe => ["-s", normalized, "shell", "echo", "ok"],
            ExecutionAndroidAction.Screenshot => ["-s", normalized, "exec-out", "screencap", "-p"],
            _ => throw new ArgumentException("execution_android_action_invalid")
        };
        if (action == ExecutionAndroidAction.StopApplication && Protected(package)) throw new InvalidOperationException("execution_android_package_protected");
        return new(action, arguments);
    }

    internal static ExecutionAndroidCommand DeriveFrozen(NexusPipeline.Platform.Serialization.Protocol.ExecutionTargetAndroid frozen,
        ExecutionAndroidAction action)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        if (frozen.Tool is null || frozen.Endpoint != Endpoint(frozen.Endpoint))
            throw new ArgumentException("execution_android_frozen_target_invalid");
        if (frozen.Driver == "genericAdb")
        {
            if (frozen.Mapping is not null) throw new ArgumentException("execution_android_mapping_unexpected");
            if (frozen.Emulator is { } emulator)
            {
                var selected = action == ExecutionAndroidAction.LaunchDevice ? emulator.Launch : action == ExecutionAndroidAction.Shutdown ? emulator.Quit : null;
                if (selected is not null) return new(action, selected.Arguments.ToImmutableArray());
            }
            return Generic(frozen.Endpoint, action, frozen.StartArguments, frozen.Package);
        }
        if (frozen.Driver != "mumu" || frozen.Mapping is not { InfoEvidence: not null } mapping || mapping.Index is < 0 or > int.MaxValue ||
            mapping.ManagerImageSha256 != frozen.Tool.Sha256 || mapping.EndpointPort != new Uri("http://" + Endpoint(frozen.Endpoint)).Port ||
            !new Uri("http://" + Endpoint(frozen.Endpoint)).IsLoopback)
            throw new ArgumentException("execution_mumu_mapping_unproven");
        StepEvidenceCodec.Plan(new(mapping.InfoEvidence.RelativePath, mapping.InfoEvidence.SizeBytes, mapping.InfoEvidence.Sha256));
        return MuMuArguments(mapping.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), action, frozen.StartArguments, frozen.Package);
    }

    private static ExecutionAndroidCommand MuMuArguments(string index, ExecutionAndroidAction action,
        IReadOnlyList<string> startArguments, string package)
    {
        Require(action, startArguments, package);
        if (action == ExecutionAndroidAction.StopApplication && Protected(package)) throw new InvalidOperationException("execution_android_package_protected");
        ImmutableArray<string> arguments = action switch
        {
            ExecutionAndroidAction.Connect => ["adb", "-v", index, "connect"],
            ExecutionAndroidAction.LaunchDevice => ["control", "-v", index, "launch"],
            ExecutionAndroidAction.DeviceState => ["info", "-v", index],
            ExecutionAndroidAction.StartApplication => ImmutableArray.Create("adb", "-v", index, "shell", "am", "start").AddRange(startArguments),
            ExecutionAndroidAction.Foreground => ["adb", "-v", index, "shell", "dumpsys", "window"],
            ExecutionAndroidAction.StopApplication => ["api", "-v", index, "close_app", package],
            ExecutionAndroidAction.Shutdown => ["control", "-v", index, "shutdown"],
            ExecutionAndroidAction.OfflineProbe => ["info", "-v", index],
            ExecutionAndroidAction.Screenshot => ["adb", "-v", index, "exec-out", "screencap", "-p"],
            _ => throw new ArgumentException("execution_android_action_invalid")
        };
        return new(action, arguments);
    }

    internal static ImmutableArray<string> MuMuInfo => ["info", "-v", "all"];

    private static void Require(ExecutionAndroidAction action, IReadOnlyList<string> arguments, string package)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(package);
        if (!Enum.IsDefined(action) || arguments.Count > 256 || arguments.Any(value => value is null || value.Length is < 1 or > 4096 ||
                value.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or
                    '_' or '.' or '/' or ':' or '@' or ',' or '+' or '%' or '=' or '-'))) ||
            !Regex.IsMatch(package, "^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("execution_android_command_invalid");
        if (action == ExecutionAndroidAction.StartApplication) RequireStartTarget(arguments, package);
    }

    private static void RequireStartTarget(IReadOnlyList<string> arguments, string package)
    {
        var found = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var option = arguments[index];
            var count = option switch
            {
                "-n" or "--component" or "-p" or "--package" or "-a" or "-c" or "-d" or "-t" or "-f" => 1,
                "--es" or "--ei" or "--ez" or "--el" => 2,
                "-W" or "-S" => 0,
                _ => throw new ArgumentException("execution_android_start_option_unavailable")
            };
            if (arguments.Count - index - 1 < count) throw new ArgumentException("execution_android_start_option_invalid");
            if (option is "-n" or "--component")
            {
                var component = arguments[index + 1];
                var slash = component.IndexOf('/');
                if (slash < 1 || slash == component.Length - 1 || component[..slash] != package)
                    throw new ArgumentException("execution_android_start_package_changed");
                found = true;
            }
            else if (option is "-p" or "--package")
            {
                if (arguments[index + 1] != package) throw new ArgumentException("execution_android_start_package_changed");
                found = true;
            }
            index += count;
        }
        if (!found) throw new ArgumentException("execution_android_start_package_unproven");
    }

    private static bool Protected(string package) => package is "com.android.systemui" or "com.android.launcher" or "app.lawnchair" or "com.mumu.launcher";
}
