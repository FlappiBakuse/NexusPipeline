using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Extensibility.Execution;

internal static class ProjectReaderFixture
{
    internal static async Task ReadAsync(ExecutionProviderChannel channel, string marker, CancellationToken cancellation)
    {
        using var configuration = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(channel.Bootstrap.ConfigurationTargets.Single(), "local.json"), cancellation));
        if (configuration.RootElement.GetProperty("account").GetString() != "saved-account" ||
            configuration.RootElement.GetProperty("TaskItems").GetArrayLength() != 0) throw new IOException("project_fixture_configuration_unbound");
        await using var reader = await ExecutionProjectReader.ConnectAsync(channel.Bootstrap, cancellation);
        var manifest = await reader.ManifestAsync(cancellation);
        if (manifest.RootIndex != "root" || manifest.AssetCount != 3 || manifest.Trees is not [{ Name: "resources", Path: "assets" }])
            throw new IOException("project_fixture_manifest_unbound");
        if (manifest.Selection is not { ConsumerExtensionId: "maas" } selected || !Guid.TryParse(selected.BindingId, out var binding) || binding == Guid.Empty)
            throw new IOException("project_fixture_selection_unbound");
        var assets = (await reader.AssetsAsync(0, cancellation)).Assets ?? throw new IOException("project_fixture_assets_missing");
        if (assets.Count != 3 || (await reader.AssetsAsync(3, cancellation)).Assets?.Count != 0) throw new IOException("project_fixture_paging_invalid");
        var artifact = assets.Single(item => item.Name == "artifact");
        using var content = new MemoryStream();
        for (long offset = 0; offset < artifact.SizeBytes; offset += 131072)
        {
            var chunk = await reader.ReadAssetAsync("artifact", offset, 131072, cancellation);
            if (chunk.Sha256 != artifact.Sha256 || chunk.SizeBytes != artifact.SizeBytes) throw new IOException("project_fixture_chunk_unbound");
            content.Write(Convert.FromBase64String(chunk.ContentBase64!));
        }
        if (Convert.ToHexStringLower(SHA256.HashData(content.ToArray())) != artifact.Sha256) throw new IOException("project_fixture_digest_invalid");
        using var json = JsonDocument.Parse(content.ToArray());
        if (json.RootElement.GetProperty("value").GetProperty("token").GetInt32() != 42) throw new IOException("project_fixture_content_invalid");
        var entries = (await reader.TreeAsync("resources", 0, cancellation)).Entries!;
        if (entries.Count != 3 || !entries.Any(item => item.Path == "image.png" && item.Kind == "file")) throw new IOException("project_fixture_tree_invalid");
        var image = await reader.ReadFileAsync("resources", "image.png", 0, 131072, cancellation);
        if (!Convert.FromBase64String(image.ContentBase64!).AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 })) throw new IOException("project_fixture_file_invalid");
        using var workspace = channel.OpenWorkspace();
        workspace.RequireCurrent();
        await File.WriteAllBytesAsync(Path.Combine(workspace.InputDirectory, "image.png"), Convert.FromBase64String(image.ContentBase64!), cancellation);
        await File.WriteAllTextAsync(Path.Combine(workspace.OutputDirectory, "result.txt"), "native-fixture-output", cancellation);
        try { using var duplicate = channel.OpenWorkspace(); throw new InvalidOperationException("workspace_duplicate_accepted"); }
        catch (IOException) { }
        try { Directory.Move(workspace.Root, workspace.Root + ".moved"); throw new InvalidOperationException("workspace_replacement_accepted"); }
        catch (IOException) { }
        workspace.RequireCurrent();
        await File.WriteAllTextAsync(marker + ".project", JsonSerializer.Serialize(new
            { manifest.Fingerprint, artifact.SizeBytes, artifact.Sha256, resourceSha256 = image.Sha256, processId = Environment.ProcessId,
                selection = manifest.Selection, workspace = channel.Bootstrap.Workspace }), cancellation);
    }
}
