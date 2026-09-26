namespace NexusPipeline.Platform.Processes;

/// <summary>Role assigned by a frozen launch contract; ownership and identity are checked separately.</summary>
internal enum ProcessRole
{
    AutomationWorker,
    GameLauncher,
    ConfigurationWriter,
    Game,
    ObservationSidecar,
    Unknown,
}

internal static class ProcessRoleClassifier
{
    /// <summary>Windows console host is an OS sidecar of a retained console launcher.</summary>
    public static bool IsConsoleSidecar(ProcessIdentity identity)
    {
        try
        {
            return string.Equals(Path.GetFullPath(identity.ImageName),
                Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
