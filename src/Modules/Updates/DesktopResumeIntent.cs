namespace NexusPipeline.Modules.Updates;

internal sealed record DesktopResumeIntent(int SchemaVersion, string Mode)
{
    internal static DesktopResumeIntent FromVisible(bool visible) => new(1, visible ? "show" : "background");
    internal void Validate()
    {
        if (SchemaVersion != 1 || Mode is not ("show" or "background"))
            throw new InvalidDataException("update.desktop_intent_invalid");
    }
}

internal enum UpdateWorkerLaunch { NotStarted, Started, Unconfirmed }
