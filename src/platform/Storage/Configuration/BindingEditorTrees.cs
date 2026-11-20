using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class BindingEditorTrees
{
    internal static FileTreeSnapshot Missing { get; } = new(FileObjectKind.Missing, []);

    internal static bool Within(string parent, string path) => parent.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    internal static string Relative(string parent, string path) => parent.Length == path.Length ? "" : path[(parent.Length + 1)..];

    internal static FileTreeSnapshot File(FileTreeSnapshot tree, string relative)
    {
        var entry = tree.Entries.SingleOrDefault(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (entry?.Kind != FileObjectKind.File) throw new ConfigurationException("configuration_edit_required_document_missing");
        return new(FileObjectKind.File, [entry with { RelativePath = "" }]);
    }

    internal static FileTreeSnapshot Remove(FileTreeSnapshot tree, string relative)
    {
        if (relative.Length == 0) return Missing;
        return tree with { Entries = tree.Entries.Where(item => !Within(relative, item.RelativePath)).ToArray() };
    }

    internal static FileTreeSnapshot Put(FileTreeSnapshot tree, string relative, FileTreeSnapshot file)
    {
        if (relative.Length == 0) return file;
        if (tree.Shape == FileObjectKind.File || file.Shape != FileObjectKind.File)
            throw new ConfigurationException("configuration_edit_document_shape_invalid");
        var entries = Remove(tree, relative).Entries.ToList();
        entries.Add(file.Entries.Single() with { RelativePath = relative });
        var directory = relative;
        while (directory.Length != 0)
        {
            var slash = directory.LastIndexOf('/');
            directory = slash < 0 ? "" : directory[..slash];
            var entry = entries.SingleOrDefault(item => item.RelativePath.Equals(directory, StringComparison.OrdinalIgnoreCase));
            if (entry is null) entries.Add(new(directory, FileObjectKind.Directory, 0, null));
            else if (entry.Kind != FileObjectKind.Directory) throw new ConfigurationException("configuration_edit_document_shape_invalid");
        }
        return FileTreePolicy.Freeze(new(FileObjectKind.Directory, entries));
    }

    internal static FileTreeSnapshot SelectInstance(ImmutableBlobStore blobs, FileTreeSnapshot file, ScriptSourceInstance instance)
    {
        var entry = file.Entries.Single();
        if (entry.SizeBytes > StrictJson.MaximumBytes) throw new ConfigurationException("configuration_document_limit");
        using var source = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
        var bytes = new byte[checked((int)source.Length)];
        try
        {
            source.ReadExactly(bytes);
            using var parsed = StrictJson.Parse(bytes);
            var document = JsonNode.Parse(parsed.RootElement.GetRawText()) as JsonObject;
            if (document?[instance.Collection] is not JsonArray rows)
                throw new ConfigurationException("configuration_editor_instance_invalid");
            var selected = rows.OfType<JsonObject>().Where(row => row[instance.Identity] is JsonValue value &&
                value.TryGetValue<string>(out var id) && id == instance.Value).ToArray();
            if (selected.Length != 1) throw new ConfigurationException("configuration_editor_instance_invalid");
            document[instance.Collection] = new JsonArray(selected[0].DeepClone());
            SetSelection(document, instance);
            var content = blobs.PersistContent(BoundedJson.Serialize(document), default);
            return new(FileObjectKind.File, [new("", FileObjectKind.File, content.SizeBytes, content.Sha256)]);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void SetSelection(JsonObject document, ScriptSourceInstance instance)
    {
        var parent = document;
        foreach (var key in instance.Selection.SkipLast(1))
        {
            if (parent[key] is null) parent[key] = new JsonObject();
            parent = parent[key] as JsonObject ?? throw new ConfigurationException("configuration_editor_instance_invalid");
        }
        parent[instance.Selection[^1]] = instance.Value;
    }
}
