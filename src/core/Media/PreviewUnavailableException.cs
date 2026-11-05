namespace NexusPipeline.Core.Media;

public enum PreviewAvailability { WaitingForGame, WindowNotReady, EmulatorNotReady, Unavailable }

public sealed class PreviewUnavailableException(PreviewAvailability state) : Exception("media_preview_not_ready")
{
    public PreviewAvailability State { get; } = state;
}
