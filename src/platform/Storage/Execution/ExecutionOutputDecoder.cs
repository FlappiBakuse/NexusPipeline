using System.Text;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionOutputDecoder(bool files, Action<string> observe, Action<string> fail,
    Action<int, string>? observeSource = null)
{
    private sealed class Source
    {
        internal readonly Decoder Decoder = new UTF8Encoding(false, true).GetDecoder();
        internal readonly StringBuilder Line = new();
        internal bool Invalid;
    }
    private readonly Dictionary<int, Source> _sources = [];
    private readonly char[] _characters = new char[8192];

    internal void Write(int id, ReadOnlySpan<byte> bytes, bool final = false, bool flushPartial = true)
    {
        if (files != (id >= 0)) return;
        if (!_sources.TryGetValue(id, out var state)) _sources.Add(id, state = new());
        if (state.Invalid) return;
        if (final && !flushPartial) { _sources.Remove(id); return; }
        try
        {
            var position = 0; bool completed;
            do
            {
                state.Decoder.Convert(bytes[position..], _characters, final, out var used, out var count, out completed);
                position += used;
                foreach (var character in _characters.AsSpan(0, count))
                {
                    if (character == '\n') Finish(id, state);
                    else
                    {
                        if (state.Line.Length == 4096) { state.Invalid = true; fail("execution_output_line_exceeded"); return; }
                        state.Line.Append(character);
                    }
                }
                if (!completed && used == 0 && count == 0) throw new IOException("execution_output_decoder_stalled");
            } while (!completed);
            if (final && state.Line.Length != 0) Finish(id, state);
        }
        catch (DecoderFallbackException) { state.Invalid = true; fail("execution_output_encoding_invalid"); }
    }

    private void Finish(int id, Source state)
    {
        var text = state.Line.ToString(); state.Line.Clear();
        var line = text.EndsWith('\r') ? text[..^1] : text;
        observe(line); observeSource?.Invoke(id, line);
    }

    internal void FlushObservedTails()
    {
        foreach (var source in _sources.Keys.ToArray()) Write(source, [], final: true);
    }
}
