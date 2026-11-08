using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage;

internal sealed class StartupProfile(string phase, string? extensionId = null) : IDisposable
{
    private static readonly AsyncLocal<StartupProfile?> Current = new();
    private readonly StartupProfile? _previous = Current.Value;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly ConcurrentDictionary<string, (long Count, double Milliseconds)> _steps = new();
    internal static StartupProfile Begin(string phase, string? extensionId = null)
    {
        var profile = new StartupProfile(phase, extensionId);
        Current.Value = profile;
        return profile;
    }
    internal static Measurement Measure(string name) => new(Current.Value, name);
    internal readonly struct Measurement(StartupProfile? owner, string name) : IDisposable
    {
        private readonly long _started = owner is null ? 0 : Stopwatch.GetTimestamp();
        public void Dispose()
        {
            if (owner is null) return;
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            owner._steps.AddOrUpdate(name, (1, elapsed), (_, prior) => (prior.Count + 1, prior.Milliseconds + elapsed));
        }
    }
    public void Dispose()
    {
        Current.Value = _previous;
        Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "startup.profile", phase, extensionId,
            elapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            steps = _steps.OrderBy(pair => pair.Key).Select(pair => new { name = pair.Key,
                count = pair.Value.Count, totalMilliseconds = pair.Value.Milliseconds }) }));
    }
}
