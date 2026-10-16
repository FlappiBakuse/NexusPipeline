namespace NexusPipeline.Modules.Updates;
internal sealed record UpdateOptions(string? UpdateSourceUrl, string UpdateChannel);
internal sealed class UpdateAdmissionLease : IDisposable
{
    private Action? _release;
    internal UpdateAdmissionLease(Action? release = null) => _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
