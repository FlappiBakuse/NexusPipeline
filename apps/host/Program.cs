using System.Reflection;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;

if (args.Length == 0 && OperatingSystem.IsWindows())
{
    var application = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(Environment.ProcessPath), ApplicationTree.HostEntry))
    {
        Console.Error.WriteLine("Start the assembled application or supply an explicit runtime root.");
        return 2;
    }
    args = ["desktop", "--runtime-root", Path.Combine(application, "runtime"), "--port", "saved"];
}

if (args is ["install-bundle", "--runtime-root", var installationRoot, "--bundle", var bundleRoot] && OperatingSystem.IsWindows())
    return await ApplicationHost.RunAsync(installationRoot, 0, installationBundle: bundleRoot);

if (args is ["--mumu-live-capture-worker"] && OperatingSystem.IsWindows())
    return await NexusPipeline.Platform.Media.MuMuLiveWorker.RunAsync();

if (args is ["--mumu-capture-worker"] && OperatingSystem.IsWindows())
    return await NexusPipeline.Platform.Media.MuMuCaptureWorker.RunAsync();

if (args is ["--window-capture-worker"] && OperatingSystem.IsWindows())
    return await NexusPipeline.Platform.Media.WindowCaptureWorker.RunAsync();

if (args is ["--execution-judgement-worker"] && OperatingSystem.IsWindows())
    return await JudgementWorkerHost.RunAsync();

if (args is ["control", .. var command])
{
    Console.InputEncoding = new System.Text.UTF8Encoding(false);
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    return command is ["menu", .. var menu]
        ? await NexusPipeline.Host.Control.Cli.ControlMenu.RunAsync(menu)
        : await NexusPipeline.Host.Control.Cli.ControlCli.RunAsync(command);
}

if (args is ["maintenance-start", ..] && OperatingSystem.IsWindows())
    return await NexusPipeline.Host.Lifecycle.MaintenanceHostStart.RunAsync(args);

if (args is ["restart-start", ..] && OperatingSystem.IsWindows())
    return await NexusPipeline.Host.Lifecycle.HostRestartStart.RunAsync(args);

if (args is ["--version"])
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version);
    return 0;
}

if (OperatingSystem.IsWindows() && (args.Length == 5 || args.Length == 7 && args[5] == "--native-client-pid" &&
     int.TryParse(args[6], out var parsedPid) && parsedPid > 0) &&
    args is [var entry, "--runtime-root", var runtimeRoot, "--port", var portValue, ..] &&
    (entry == "serve" || entry == "desktop" && args.Length == 5) &&
    (portValue == "saved" || int.TryParse(portValue, out var requestedPort) && requestedPort is >= 0 and <= 65535))
{
    try
    {
        int? port = portValue == "saved" ? null : int.Parse(portValue, System.Globalization.CultureInfo.InvariantCulture);
        return await ApplicationHost.RunAsync(runtimeRoot, port, args.Length == 7 ? int.Parse(args[6]) : null, launchDesktop: entry == "desktop");
    }
    catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or ArgumentException or
        System.ComponentModel.Win32Exception or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Host initialization rejected an invalid or unavailable input.");
        return 1;
    }
}

Console.Error.WriteLine("Usage: --version | control <command> | <serve|desktop> --runtime-root <root> --port <saved|0..65535> [--native-client-pid <trusted-owner>]");
return 2;
