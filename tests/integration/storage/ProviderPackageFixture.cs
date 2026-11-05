using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ProviderPackageFixture : IDisposable
{
    internal string Root { get; }
    internal string Deployed { get; }
    internal string Archive { get; }
    internal string Worker { get; }
    internal byte[] ManifestBytes { get; }
    internal string? Launcher { get; }

    internal ProviderPackageFixture(IReadOnlyList<string>? workerArguments = null, IReadOnlyList<ExecutionProviderResource>? resources = null,
        IReadOnlyList<string>? launcherArguments = null, bool invalidLauncher = false, bool launcherChild = false, bool retrySafe = false,
        bool gracefulStop = false, bool targetControl = false)
    {
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "execution-provider-package-" + Guid.NewGuid().ToString("N"));
        Deployed = Path.Combine(Root, "deployed"); Directory.CreateDirectory(Deployed);
        var source = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")
            ?? throw new InvalidOperationException("NEXUS_PROCESS_FIXTURE_EXE_required");
        var runtime = JudgementInterpreterLease.Inspect(source);
        foreach (var relative in runtime.RuntimeFiles.Select(file => file.RelativePath).Append(Path.GetFileName(source)))
        {
            var destination = Path.Combine(Deployed, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, relative), destination);
        }
        Worker = Path.Combine(Deployed, Path.GetFileName(source));
        if (launcherArguments is not null)
        {
            var game = Environment.GetEnvironmentVariable("NEXUS_TARGET_GAME_FIXTURE_EXE")
                ?? throw new InvalidOperationException("NEXUS_TARGET_GAME_FIXTURE_EXE_required");
            var frozen = JudgementInterpreterLease.Inspect(game);
            foreach (var relative in frozen.RuntimeFiles.Select(file => file.RelativePath).Append(Path.GetFileName(game)))
            {
                var destination = Path.Combine(Deployed, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var original = Path.Combine(Path.GetDirectoryName(game)!, relative);
                if (File.Exists(destination))
                {
                    if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(original))) throw new IOException("fixture_runtime_asset_collision");
                }
                else File.Copy(original, destination);
            }
            Launcher = Path.Combine(Deployed, Path.GetFileName(game));
            if (launcherChild)
            {
                foreach (var relative in frozen.RuntimeFiles.Select(file => file.RelativePath).Append(Path.GetFileName(game)))
                {
                    var target = Path.Combine(Deployed, "other-game", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(Path.GetDirectoryName(game)!, relative), target);
                }
                launcherArguments = ["--launch-child", Path.Combine(Deployed, "other-game", Path.GetFileName(game)),
                    launcherArguments[0], launcherArguments[1]];
            }
            if (invalidLauncher) File.WriteAllText(Launcher, "Controlled invalid PE image");
        }
        ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.extension.manifest", version = 1, extensionId = "provider-fixture", artifactName = "ProviderFixture",
            displayName = "Controlled provider fixture", packageVersion = "0.5.0", kind = "managed-code",
            minHostVersion = "0.18.0", sdkVersion = SdkIdentity.Version, contractFingerprint = SdkIdentity.ContractFingerprint,
            capabilities = new[] { "execution.drive" }, permissions = new { processAccess = new[] { "owned-worker", "selected-upstream" } },
            managed = new { entryAssembly = Path.GetFileNameWithoutExtension(source) + ".dll", entryType = "ControlledFixture" },
            execution = new { entryId = "main", workerEntry = Path.GetFileName(source), arguments = workerArguments?.ToArray() ?? Array.Empty<string>(),
                gracefulStop = gracefulStop ? true : (bool?)null,
                targetControl = targetControl ? true : (bool?)null,
                protocol = "nexus.execution.provider/1",
                retry = retrySafe ? new { safety = "safe", scope = "workerAndUpstream", idempotencyProtocol = "stable-run-binding/1", outcomes = new[] { "failed", "interrupted" } } : null,
                configurationResources = resources?.ToArray() ?? [],
                launcher = launcherArguments is null ? null : new { entry = Path.GetFileName(Launcher!), arguments = launcherArguments,
                    writesManagedConfiguration = false, terminalProtocol = "worker-terminal/1" } }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        File.WriteAllBytes(Path.Combine(Deployed, "manifest.json"), ManifestBytes);
        Archive = Path.Combine(Root, "provider.zip");
        ZipFile.CreateFromDirectory(Deployed, Archive, CompressionLevel.NoCompression, includeBaseDirectory: false);
    }

    internal VerifiedProviderPackage Verify() =>
        VerifiedProviderPackage.VerifyControlled(Archive, Deployed, "provider-fixture", new());

    internal string Fingerprint() => string.Join("\n", Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            Path.GetRelativePath(Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));

    public void Dispose() { }
}
