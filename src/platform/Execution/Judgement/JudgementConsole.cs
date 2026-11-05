using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace NexusPipeline.Platform.Execution.Judgement;

internal sealed record JudgementConsoleEntry(string Level, string Text);

internal sealed class JudgementConsole
{
    internal const int MaximumBytes = 16384;
    internal const int MaximumEntries = 128;
    private readonly List<JudgementConsoleEntry> _entries = [];
    private int _bytes;
    internal IReadOnlyList<JudgementConsoleEntry> Entries => _entries.ToArray();

    internal void Add(string level, string text)
    {
        var size = JudgementWorkerProtocol.Utf8.GetByteCount(text);
        if (_entries.Count == MaximumEntries || size > MaximumBytes - _bytes)
            throw new IOException("judgement_console_capacity_exceeded");
        _entries.Add(new(level, text)); _bytes += size;
    }

    internal void Import(IReadOnlyList<JudgementConsoleEntry> entries)
    {
        Require(entries);
        foreach (var entry in entries) Add(entry.Level, entry.Text);
    }

    internal static void Require(IReadOnlyList<JudgementConsoleEntry>? entries)
    {
        if (entries is null || entries.Count > MaximumEntries) throw Invalid();
        var bytes = 0;
        foreach (var entry in entries)
        {
            if (entry is null || entry.Level is not ("log" or "info" or "warn" or "error" or "debug") || entry.Text is null)
                throw Invalid();
            bytes = checked(bytes + JudgementWorkerProtocol.Utf8.GetByteCount(entry.Text));
            if (bytes > MaximumBytes) throw Invalid();
        }
    }

    internal void Install(Engine engine, CancellationToken cancellation)
    {
        var console = engine.Intrinsics.Object.Construct([]);
        foreach (var level in new[] { "log", "info", "warn", "error", "debug" })
            console.CreateDataProperty(level, new ClrFunction(engine, level, (_, arguments) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (arguments.Length > MaximumEntries) throw new IOException("judgement_console_capacity_exceeded");
                var parts = new List<string>(); var bytes = 0;
                foreach (var argument in arguments)
                {
                    var text = TypeConverter.ToString(argument); bytes = checked(bytes + JudgementWorkerProtocol.Utf8.GetByteCount(text));
                    if (bytes + Math.Max(0, arguments.Length - 1) > MaximumBytes) throw new IOException("judgement_console_capacity_exceeded");
                    parts.Add(text);
                }
                Add(level, string.Join(' ', parts)); return JsValue.Undefined;
            }));
        engine.SetValue("console", console); engine.Execute("Object.freeze(console);");
    }

    private static JsonException Invalid() => new("judgement_console_unrecognized");
}
