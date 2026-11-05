namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ExtensionDataNames
{
    internal static void Require(string name)
    {
        if (name is not { Length: >= 1 and <= 80 } || name.Any(character =>
            character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '.' and not '_'))
            throw new ArgumentException("extension_document_name_invalid");
    }
}
