using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Extensibility;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;

internal static class BetterGiPluginFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var input, var report]) return 2;
        VerifyPath(input);
        VerifyPath(report);
        if (Path.GetDirectoryName(Path.GetFullPath(input)) != Path.GetDirectoryName(Path.GetFullPath(report)))
            throw new IOException("BetterGI report left its input directory");
        using var settings = StrictJson.Parse(File.ReadAllBytes(input));
        var root = settings.RootElement;
        var manifest = ReadAsset(root, "manifest", "manifestSha256");
        var scriptBytes = ReadAsset(root, "script", "scriptSha256");
        var script = new System.Text.UTF8Encoding(false, true).GetString(scriptBytes);
        var instance = root.GetProperty("instanceId").GetGuid();
        var run = root.GetProperty("runId").GetGuid();
        var attempt = root.GetProperty("attemptId").GetGuid();
        if (new[] { instance, run, attempt }.Contains(Guid.Empty)) throw new IOException("BetterGI fixture identity empty");
        var reader = new ContractReader();
        var admitted = new ExtensionManifestAdmission(reader).ReadCurrent(manifest, "bettergi");
        await using var scope = await new ExtensionActivator(reader).ActivateSpecializedAsync(manifest, "bettergi", static (_, _, _) => ValueTask.CompletedTask, default);
        var executor = new TaskAdapterExecutor(manifest, scope, reader, admitted.Specialized!.AdapterEntry, script, root.GetProperty("scriptSha256").GetString()!);
        var proof = new BetterGiProtocolProof(executor, run, attempt);
        proof.Run();
        if (!manifest.AsSpan().SequenceEqual(ReadAsset(root, "manifest", "manifestSha256")) ||
            !scriptBytes.AsSpan().SequenceEqual(ReadAsset(root, "script", "scriptSha256")))
            throw new IOException("BetterGI source assets changed during invocation");
        var result = new { status = "PASS", scope = "Actual specialized script/public SDK and bounded Host Jint interpreter in owned fixture worker; controlled upstream config/logs only",
            processId = Environment.ProcessId, context = new { instanceId = instance, runId = run, attemptId = attempt },
            contractFingerprint = SdkIdentity.ContractFingerprint, scriptSha256 = root.GetProperty("scriptSha256").GetString(),
            checks = proof.Checks, checkCount = proof.Checks.Count, upstreamBinary = "NOT_RUN", persistentExecution = "NOT_RUN", browserSurface = "NOT_RUN" };
        File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(result, JsonSerializerOptions.Web));
        return 0;
    }

    private static byte[] ReadAsset(JsonElement input, string pathKey, string digestKey)
    {
        var path = input.GetProperty(pathKey).GetString()!;
        VerifyPath(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 262144 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != input.GetProperty(digestKey).GetString())
            throw new IOException("BetterGI asset differs from its source-bound identity");
        return bytes;
    }

    private static void VerifyPath(string path)
    {
        var owned = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("BetterGI fixture root required");
        if (!Path.IsPathFullyQualified(owned) || !Path.IsPathFullyQualified(path)) throw new IOException("BetterGI fixture paths must be absolute");
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(owned).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar) ||
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("BetterGI fixture path left the owned temporary round");
        for (FileSystemInfo? item = new FileInfo(full); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("BetterGI fixture path contains a reparse point");
    }
}
