using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Media;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Media;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;

internal static class MaaPluginFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath, var reportPath]) return 2;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing fixture root"));
        reportPath = Owned(root, reportPath);
        try
        {
            var firstPath = reportPath + ".cycle1.json";
            var result = await RunVerifiedAsync([inputPath, firstPath]);
            using var first = JsonDocument.Parse(File.ReadAllBytes(firstPath));
            var cycles = new List<JsonElement> { first.RootElement.Clone() };
            if (first.RootElement.GetProperty("scenario").GetString() == "positive")
            {
                var secondPath = reportPath + ".cycle2.json";
                result = await RunVerifiedAsync([inputPath, secondPath]);
                using var second = JsonDocument.Parse(File.ReadAllBytes(secondPath));
                cycles.Add(second.RootElement.Clone());
            }
            Write(reportPath, new { status = "PASS", cycles,
                scope = "Component-only fixed SDK context with fixture-mapped assets; actual installed loader is verified separately" });
            return result;
        }
        catch (Exception exception)
        {
            using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(JsonSerializer.SerializeToUtf8Bytes(new { status = "FAIL", exception = exception.ToString() }, JsonSerializerOptions.Web));
            return 1;
        }
    }

    private static async Task<int> RunVerifiedAsync(string[] args)
    {
        var inputPath = args[0];
        var reportPath = args[1];
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("Missing fixture root"));
        using var input = JsonDocument.Parse(File.ReadAllBytes(Owned(root, inputPath)));
        var settings = input.RootElement;
        var assemblyPath = Owned(root, settings.GetProperty("assembly").GetString()!);
        var manifestPath = Owned(root, settings.GetProperty("manifest").GetString()!);
        var runtimeLibrary = Owned(root, settings.GetProperty("runtimeLibrary").GetString()!);
        var resource = Owned(root, settings.GetProperty("resourceRoot").GetString()!);
        var imagePath = Owned(root, settings.GetProperty("image").GetString()!);
        var logs = Owned(root, settings.GetProperty("logRoot").GetString()!);
        reportPath = Owned(root, reportPath);
        var reader = new ContractReader();
        using var plugin = new PluginSliceAssembly(assemblyPath, settings.GetProperty("assemblySha256").GetString()!, manifestPath, "maa-framework", reader);
        var manifestBytes = plugin.ManifestBytes;
        var sessionType = plugin.Assembly.GetType("NexusPipeline.Extensions.MaaFramework.MaaImageSession", true)!;
        var controllerPath = Path.Combine(Path.GetDirectoryName(runtimeLibrary)!, "MaaCustomControlUnit.dll");
        Require(Hash(runtimeLibrary) == settings.GetProperty("runtimeSha256").GetString() &&
            Hash(controllerPath) == settings.GetProperty("controllerSha256").GetString(), "Native input identity differs");
        var configuration = Path.Combine(Path.GetDirectoryName(reportPath)!, "maa-runtime-" + Guid.NewGuid().ToString("N") + ".json");
        Write(configuration, new { format = "nexus.maa.runtime", version = 1,
            runtimeLibrary = "runtime/MaaFramework.dll", controllerLibrary = "runtime/MaaCustomControlUnit.dll",
            resourceDirectory = "resources", image = "preview.png" });
        IExtension? extension = null;
        var context = new RunMediaContext(settings.GetProperty("instanceId").GetGuid(), settings.GetProperty("runId").GetGuid(), settings.GetProperty("attemptId").GetGuid());
        var registry = new MediaPreviewRegistry(new Authorization(context));
        var extensionType = plugin.ExtensionType;
        var invalid = settings.GetProperty("scenario").GetString() == "invalid-resource";
        ExtensionScope? activation = null;
        var resourceRejected = false;
        var disposedAfterFailure = false;
        try
        {
            activation = await new ExtensionActivator(reader).ActivateManagedAsync(manifestBytes, "maa-framework", (descriptor, scope) =>
            {
                var assets = new MaaAssetFixture(scope, new()
                {
                    ["maa.runtime.json"] = configuration, ["runtime/MaaFramework.dll"] = runtimeLibrary,
                    ["runtime/MaaCustomControlUnit.dll"] = controllerPath, ["preview.png"] = imagePath
                }, resource, logs);
                extension = (IExtension)Activator.CreateInstance(extensionType,
                    SliceActivationContext.Create(previews: new MediaRegistrationPort(descriptor, scope, registry, reader),
                        assets: assets, outputs: assets))!;
                return extension;
            }, default);
        }
        catch (IOException exception) when (invalid && exception.Message == "maa_initialize_resource_bundle_failed" &&
            exception.InnerException is IOException { Message: "maa_resource_load_failed" })
        {
            resourceRejected = true;
            try { await ((IMediaFrameProvider)Session()).CaptureAsync(Request(context), default); }
            catch (ObjectDisposedException) { disposedAfterFailure = true; }
            var hidden = false;
            try { using var unexpected = registry.Open(context, "maa-framework", "native-preview"); }
            catch (InvalidOperationException) { hidden = true; }
            Require(hidden && disposedAfterFailure, "Failed native activation retained published or live resources");
        }
        var session = Session();
        var provider = (IMediaFrameProvider)session;
        var resourceStatus = (int)sessionType.GetProperty("ResourceStatus")!.GetValue(session)!;
        if (invalid)
        {
            Require(resourceRejected && resourceStatus == 4000, "Invalid resource was not rejected by the native parser");
            Write(reportPath, new { status = "PASS", scenario = "invalid-resource", resourceStatus, resourceRejected, disposedAfterFailure,
                processId = Environment.ProcessId, context, assemblySha256 = plugin.AssemblySha256, references = plugin.References, modules = Modules(Path.GetDirectoryName(runtimeLibrary)!) });
            return 0;
        }
        await using var lifetime = activation ?? throw new IOException("Native extension did not activate");
        var completion = sessionType.GetMethod("ExecuteTask")!.Invoke(session, ["FixtureDirectHit", CancellationToken.None])!;
        using var completed = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(completion, JsonSerializerOptions.Web));
        Require(completed.RootElement.GetProperty("nativeStatus").GetInt32() == 3000 && completed.RootElement.GetProperty("taskId").GetInt64() > 0,
            "Actual native task failed");
        using var preview = registry.Open(context, "maa-framework", "native-preview");
        var frame = await preview.CaptureAsync();
        Require(frame.Width == 64 && frame.Height == 64, "Native frame dimensions differ");
        var modules = Modules(Path.GetDirectoryName(runtimeLibrary)!);
        Require(modules.Any(item => Path.GetFileName(item.Path) == "MaaFramework.dll") && modules.Any(item => Path.GetFileName(item.Path) == "MaaCustomControlUnit.dll"),
            "Expected native engine and image controller were not loaded");
        var captureCount = (int)sessionType.GetProperty("ImageCaptureCount")!.GetValue(session)!;
        var controllerClean = (bool)sessionType.GetProperty("ImageControllerClean")!.GetValue(session)!;
        Require(captureCount > 0 && controllerClean, "Image controller attempted input or failed a native callback");
        await lifetime.DisposeAsync();
        var disposed = false;
        try { await provider.CaptureAsync(Request(context), default); }
        catch (ObjectDisposedException) { disposed = true; }
        Require(disposed, "Native media provider outlived its scope");
        var modulesAfterCleanup = Modules(Path.GetDirectoryName(runtimeLibrary)!);
        Require(!modulesAfterCleanup.Any(item => Path.GetFileName(item.Path) is "MaaFramework.dll" or "MaaCustomControlUnit.dll"),
            "Native framework or controller remained loaded after cleanup");
        Write(reportPath, new { status = "PASS", scenario = "positive", processId = Environment.ProcessId, assemblySha256 = plugin.AssemblySha256, references = plugin.References,
            nativeVersion = sessionType.GetProperty("NativeVersion")!.GetValue(session), resourceStatus,
            connectionStatus = sessionType.GetProperty("ConnectionStatus")!.GetValue(session), task = completed.RootElement.Clone(),
            frame = new { frame.Width, frame.Height, frame.SizeBytes, sha256 = Convert.ToHexStringLower(SHA256.HashData(frame.Png.Span)) },
            disposed, context, captureCount, controllerClean, modules, modulesAfterCleanup, contractFingerprint = NexusPipeline.Extensibility.SdkIdentity.ContractFingerprint,
            scope = "Actual plugin DLL/public SDK/native MaaFramework image controller and DirectHit/DoNothing fixture; no desktop, device or game control" });
        return 0;

        object Session() => extensionType.GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(extension ?? throw new IOException("Native extension was not constructed"))
            ?? throw new IOException("Native extension did not create its own session");
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static MediaCapture Request(RunMediaContext context) => new()
    {
        Format = "nexus.media.capture", Version = 1, InstanceId = context.InstanceId.ToString("D"), RunId = context.RunId.ToString("D"),
        AttemptId = context.AttemptId.ToString("D"), PreviewId = Guid.NewGuid().ToString("D"), RequestId = Guid.NewGuid().ToString("D"), Sequence = 1
    };

    private sealed record NativeModule(string Path, string Sha256);
    private static NativeModule[] Modules(string runtimeRoot)
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Where(module => module.FileName.StartsWith(runtimeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(module => new NativeModule(module.FileName, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(module.FileName))))).ToArray();
    }

    private static string Owned(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Maa fixture path leaves the owned root");
        FileSystemInfo? item = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        while (item is not null)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Maa fixture path is a reparse point");
            item = item is DirectoryInfo folder ? folder.Parent : ((FileInfo)item).Directory;
        }
        return full;
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new IOException(reason); }
    private static void Write(string path, object value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web));
    private sealed class Authorization(RunMediaContext current) : IRunMediaAuthorization
    {
        public void AssertAllowed(RunMediaContext context, string extensionId)
        {
            if (context != current || extensionId != "maa-framework") throw new UnauthorizedAccessException("Maa fixture run identity differs");
        }
    }
}
