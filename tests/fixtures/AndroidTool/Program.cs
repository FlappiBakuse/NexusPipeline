using System.Diagnostics;
using System.Text.Json;

namespace NexusPipeline.AndroidToolFixture;

internal static class Program
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<int> Main(string[] args)
    {
        var root = FixtureDirectory(AppContext.BaseDirectory);
        var temporary = FixtureDirectory(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ??
            throw new IOException("fixture_temp_root_missing"));
        var relative = Path.GetRelativePath(temporary, root);
        if (relative is "." or ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("fixture_output_outside_declared_temp");
        if (await FixtureExtensions.RunAsync(args) is { } vendorExit) return vendorExit;
        var path = Path.Combine(root, "fixture-device.json");
        var device = JsonSerializer.Deserialize<Device>(File.ReadAllBytes(path), Options) ?? throw new IOException("fixture_device_invalid");
        if (args.Length == 2 && args[0] == "--fixture-server" && args[1] == device.Nonce)
        {
            File.WriteAllText(Path.Combine(root, "server-ready"), Environment.ProcessId.ToString());
            await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
        }
        var observed = new Invocation(args, Environment.CurrentDirectory, Environment.ProcessId, device.Nonce);
        var events = Path.Combine(root, "observed"); Directory.CreateDirectory(events);
        var eventPath = Path.Combine(events, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(eventPath + ".prepared", JsonSerializer.SerializeToUtf8Bytes(observed, Options));
        File.Move(eventPath + ".prepared", eventPath);
        var action = device.Driver == "genericAdb" ? Generic(args, device) : MuMu(args, device);
        if (device.ResponsePaddingBytes is < 0 or > 262144) throw new IOException("fixture_padding_size_invalid");
        if (device.ResponsePaddingBytes > 0) Console.Write(new string(' ', device.ResponsePaddingBytes));
        if (device.DelayMilliseconds > 0) await Task.Delay(device.DelayMilliseconds);
        if (device.ResponseAction == action)
        {
            Console.OpenStandardOutput().Write(System.Text.Encoding.UTF8.GetBytes(device.ResponseStandardOutput ?? string.Empty));
            Console.OpenStandardError().Write(System.Text.Encoding.UTF8.GetBytes(device.ResponseStandardError ?? string.Empty));
            File.WriteAllText(Path.Combine(root, "response-written"), device.Nonce);
            if (device.HangAfterResponse) await Task.Delay(Timeout.InfiniteTimeSpan);
            return device.ResponseExitCode;
        }
        if (device.FailAction == action)
        {
            Console.Error.Write("fixture rejected " + action); return 37;
        }
        if (device.OverflowAction == action)
        {
            var block = new string('x', 65536);
            for (var index = 0; index < 34; index++) Console.Write(block);
            Console.Out.Flush(); return 0;
        }
        if (device.ServerAction == action)
        {
            FixtureServer.Start(device.Nonce);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(root, "server-ready")) && timer.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10);
            if (!File.Exists(Path.Combine(root, "server-ready"))) throw new IOException("fixture_server_not_ready");
        }
        switch (action)
        {
            case "input": break;
            case "device-output": Console.Write("device-fixture-output"); break;
            case "device-hang": await Task.Delay(Timeout.InfiniteTimeSpan); break;
            case "capture":
                return await FixtureScreenshot.WriteAsync(device, root);
            case "allInfo":
                Console.Write(Info(device, all: true)); break;
            case "device":
                Console.Write(device.Driver == "mumu" ? Info(device, all: false) : device.Online ? "device\r\n" : "offline\r\n");
                break;
            case "connect":
                Console.Write("connected to " + device.Endpoint); device = device with { Online = true }; break;
            case "launch":
                device = device with { Online = true }; break;
            case "foreground":
                Console.Write("mCurrentFocus=Window{a1 u0 " + (device.Foreground ?? "com.android.launcher") + "/.Main}"); break;
            case "start":
                device = device with { Foreground = device.Package }; Console.Write("Starting: Intent"); break;
            case "stop":
                device = device with { Foreground = null }; break;
            case "shutdown":
                device = device with { Online = false, Foreground = null }; break;
            case "offline":
                if (!device.Online) { Console.Error.Write("error: device offline"); return 1; }
                Console.Write("ok"); break;
            default: throw new IOException("fixture_action_unknown");
        }
        File.WriteAllBytes(path + ".prepared", JsonSerializer.SerializeToUtf8Bytes(device, Options));
        File.Move(path + ".prepared", path, overwrite: true);
        return 0;
    }

    private static string FixtureDirectory(string path)
    {
        // The native process owner passes a long-path application name through to the .NET apphost.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6 && char.IsAsciiLetter(path[4]) &&
            path[5] == ':' && path[6] == '\\') path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new IOException("fixture_directory_invalid");
        return Path.GetFullPath(path);
    }

    private static string Generic(string[] args, Device device)
    {
        if (args.SequenceEqual(new[] { "connect", device.Endpoint })) return "connect";
        if (args.Length < 3 || args[0] != "-s" || args[1] != device.Endpoint) throw new IOException("fixture_endpoint_not_explicit");
        var command = args[2..];
        if (command.SequenceEqual(new[] { "exec-out", "screencap", "-p" })) return "capture";
        if (command.SequenceEqual(new[] { "get-state" })) return "device";
        if (command.SequenceEqual(new[] { "shell", "dumpsys", "window" })) return "foreground";
        if (command.SequenceEqual(new[] { "shell", "am", "force-stop", device.Package })) return "stop";
        if (command.SequenceEqual(new[] { "shell", "reboot", "-p" })) return "shutdown";
        if (command.SequenceEqual(new[] { "shell", "echo", "ok" })) return "offline";
        if (command.SequenceEqual(new[] { "shell", "am", "start", "-n", device.Package + "/.Main" })) return "start";
        if (DeviceInputFixture.Action(command) is { } input) return input;
        throw new IOException("fixture_generic_argv_changed");
    }

    private static string MuMu(string[] args, Device device)
    {
        if (args.SequenceEqual(new[] { "info", "-v", "all" })) return "allInfo";
        if (args.Length < 3 || args[1] != "-v" || args[2] != device.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new IOException("fixture_mumu_index_changed");
        if (args[0] == "info" && args.Length == 3) return "device";
        if (args.SequenceEqual(new[] { "control", "-v", args[2], "launch" })) return "launch";
        if (args.SequenceEqual(new[] { "control", "-v", args[2], "shutdown" })) return "shutdown";
        if (args.SequenceEqual(new[] { "api", "-v", args[2], "close_app", device.Package })) return "stop";
        if (args.SequenceEqual(new[] { "adb", "-v", args[2], "connect" })) return "connect";
        if (args.SequenceEqual(new[] { "adb", "-v", args[2], "shell", "dumpsys", "window" })) return "foreground";
        if (args.SequenceEqual(new[] { "adb", "-v", args[2], "shell", "am", "start", "-n", device.Package + "/.Main" })) return "start";
        if (args[0] == "adb" && DeviceInputFixture.Action(args[3..]) is { } input) return input;
        throw new IOException("fixture_mumu_argv_changed");
    }

    private static string Info(Device device, bool all)
    {
        var value = new { adb_port = device.ReportedPort ?? device.Port, is_process_started = device.Online, is_android_started = device.Online };
        return JsonSerializer.Serialize<object>(all ? new Dictionary<string, object> { [device.Index.ToString()] = value } : value);
    }
}
