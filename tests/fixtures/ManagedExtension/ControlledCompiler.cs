using System.Text.Json;
using System.Text;
using NexusPipeline.Extensibility.Compilation;

namespace Controlled;

internal sealed class ControlledCompiler(string directory) : IProjectCompiler
{
    public async ValueTask<JsonElement> DiscoverAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken)
    {
        var root = await source.ListAsync("", cancellationToken);
        var file = await source.ReadAsync("interface.json", cancellationToken) ?? throw new IOException("fixture_source_missing");
        return JsonSerializer.SerializeToElement(new { root.Exists, file.Sha256, file.Identity, file.Path });
    }

    public async ValueTask<CompilationResult> CompileAsync(ISelectedSource source, JsonElement selection, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "compiler-started"), "started", cancellationToken);
        var file = await source.ReadAsync("interface.json", cancellationToken) ?? throw new IOException("fixture_source_missing");
        if (selection.TryGetProperty("wait", out var waiting) && waiting.GetBoolean())
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "compiler-entered"), "entered", cancellationToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            { await File.WriteAllTextAsync(Path.Combine(directory, "compiler-cancelled"), "cancelled"); throw; }
        }
        var artifact = new byte[3 * 1024 * 1024]; Array.Fill(artifact, (byte)' ');
        Encoding.UTF8.GetBytes("{\"value\":{\"token\":42,\"a/b\":{\"~\":[false,true]}},\"chunk\":\"" + new string('x', 100000) +
            "\",\"padding\":[" + string.Join(',', Enumerable.Repeat("0", 140000)) + "]}").CopyTo(artifact, 0);
        SourceFile? configuration = null;
        if (selection.TryGetProperty("mount", out var mount) && mount.GetBoolean())
        {
            var selected = source.Configurations.Single();
            configuration = await source.ReadAsync(selected.Path + "/local.json", cancellationToken);
        }
        var trees = selection.TryGetProperty("resourcePath", out var resource) ? new[] { new SourceResourceTree("resources", resource.GetString()!)
            { IncludedPaths = selection.TryGetProperty("includedPaths", out var included) ? included.EnumerateArray().Select(value => value.GetString()!).ToArray() : null } } : [];
        var preview = selection.TryGetProperty("previewText", out var text)
            ? JsonSerializer.SerializeToUtf8Bytes(new { value = text.GetString() }) : "{}"u8.ToArray();
        return new("root", [new("root", CompiledAssetKind.Index, "{}"u8.ToArray(), ["artifact", "index"]),
            new("index", CompiledAssetKind.Index, preview, []),
            new("artifact", CompiledAssetKind.Artifact, artifact, [])],
            JsonSerializer.SerializeToElement(new { file.Path, file.Sha256, configurationSha256 = configuration?.Sha256 }))
        {
            ResourceTrees = trees, PreviewIndex = "index",
            ProtectedInputs = File.Exists(Path.Combine(directory, "compiler-protected"))
                ? [new("input-fixture", ["fixture:taskA"])]
                : selection.TryGetProperty("protected", out var secret) && secret.GetBoolean() ? [new("input-fixture", ["occurrence-a"])] : null
        };
    }
}
