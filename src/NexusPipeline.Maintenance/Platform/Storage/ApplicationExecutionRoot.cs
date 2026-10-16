namespace NexusPipeline.Platform.Storage;
internal static class ApplicationExecutionRoot
{
    internal static string Root { get; private set; } = AppContext.BaseDirectory;
    internal static void ConfigureRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("worker.absolute_root_required");
        PayloadPathSafety.RequireLinkFree(root);
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    }

}
