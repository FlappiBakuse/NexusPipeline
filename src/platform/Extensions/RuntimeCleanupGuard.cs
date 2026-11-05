namespace NexusPipeline.Platform.Extensions;

internal static class RuntimeCleanupGuard
{
    internal static void RequireRelease(ExtensionScope scope)
    {
        // A failed native destructor can still own callbacks into this assembly and its protected package.
        if (scope.HasCleanupFailure) throw new IOException("extension_runtime_cleanup_retained");
    }
}
