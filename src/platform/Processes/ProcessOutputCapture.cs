using System.Text;
using System.Threading.Channels;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Processes;

public enum ProcessOutputSource { StandardOutput, StandardError }
public sealed record ProcessOutputLine(long Sequence, ProcessOutputSource Source, string Text, bool IsContinuous);
public sealed record ProcessOutputDrain(bool StandardOutputEof, bool StandardErrorEof, long DiscardedLines,
    string? FailureCode, bool DeadlineExceeded)
{
    public bool IsComplete => StandardOutputEof && StandardErrorEof && DiscardedLines == 0 && FailureCode is null && !DeadlineExceeded;
}

[SupportedOSPlatform("windows")]
public sealed class ProcessOutputCapture
{
    private readonly object _gate = new();
    private readonly Channel<ProcessOutputLine> _lines;
    private readonly int _maximumLineCharacters;
    private long _sequence;
    private long _discarded;
    private string? _failure;
    private bool _stdoutEof;
    private bool _stderrEof;
    public ChannelReader<ProcessOutputLine> Lines => _lines.Reader;
    public Task<ProcessOutputDrain> Completion { get; }

    public ProcessOutputCapture(OwnedProcessOutput output, int capacity = 128, int maximumLineCharacters = 4096)
        : this(output, null, capacity, maximumLineCharacters) { }

    internal ProcessOutputCapture(OwnedProcessOutput output, IOutputByteSink? bytes, int capacity = 128, int maximumLineCharacters = 4096)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (capacity is < 1 or > 1024 || maximumLineCharacters is < 16 or > 65536 ||
            (long)capacity * maximumLineCharacters > 1048576) throw new ArgumentOutOfRangeException(nameof(capacity));
        _maximumLineCharacters = maximumLineCharacters;
        _lines = Channel.CreateBounded<ProcessOutputLine>(new BoundedChannelOptions(capacity)
            { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        Completion = CollectAsync(output, bytes);
    }

    public async Task<ProcessOutputDrain> DrainAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        try { return await Completion.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { lock (_gate) return Snapshot(true); }
    }

    private async Task<ProcessOutputDrain> CollectAsync(OwnedProcessOutput output, IOutputByteSink? bytes)
    {
        await Task.WhenAll(PumpAsync(output.StandardOutput, ProcessOutputSource.StandardOutput, bytes),
            PumpAsync(output.StandardError, ProcessOutputSource.StandardError, bytes)).ConfigureAwait(false);
        lock (_gate)
        {
            _lines.Writer.TryComplete();
            return Snapshot(false);
        }
    }

    private async Task PumpAsync(Stream input, ProcessOutputSource source, IOutputByteSink? raw)
    {
        var line = new StringBuilder(); var overflow = false;
        var decoder = new UTF8Encoding(false, true).GetDecoder(); var invalid = false;
        var buffer = new byte[4096]; var characters = new char[4096];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                lock (_gate)
                {
                    if (count == 0)
                    {
                        raw?.Complete(source, true); Decode(0, true);
                        if (!invalid && (overflow || line.Length != 0)) FinishLine(source, line, overflow);
                        Eof(source); return;
                    }
                    if (raw is not null && !raw.TryWrite(source, buffer.AsSpan(0, count))) Fail("process_output_raw_unavailable");
                    Decode(count, false);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { lock (_gate) { raw?.Complete(source, false); Fail("process_output_read_failed"); } }

        void Decode(int count, bool final)
        {
            if (invalid) return;
            try
            {
                var offset = 0; bool completed;
                do
                {
                    decoder.Convert(buffer.AsSpan(offset, count - offset), characters, final, out var used, out var produced, out completed);
                    offset += used;
                    for (var index = 0; index < produced; index++)
                    {
                        var character = characters[index];
                        if (character == '\n') { FinishLine(source, line, overflow); line.Clear(); overflow = false; }
                        else if (!overflow)
                        {
                            if (line.Length == _maximumLineCharacters) { overflow = true; line.Clear(); Fail("process_output_line_exceeded"); }
                            else line.Append(character);
                        }
                    }
                    if (!completed && used == 0 && produced == 0) throw new IOException("process_output_decoder_stalled");
                } while (!completed);
            }
            catch (DecoderFallbackException)
            { invalid = true; line.Clear(); overflow = false; Fail("process_output_encoding_invalid"); }
        }
    }

    private void FinishLine(ProcessOutputSource source, StringBuilder line, bool overflow)
    {
        lock (_gate)
        {
            if (overflow) { _discarded++; _failure ??= "process_output_line_exceeded"; return; }
            var text = line.ToString();
            if (text.EndsWith('\r')) text = text[..^1];
            var value = new ProcessOutputLine(++_sequence, source, text, _discarded == 0 && _failure is null);
            if (!_lines.Writer.TryWrite(value)) { _discarded++; _failure ??= "process_output_queue_overflow"; }
        }
    }

    private void Fail(string reason) { lock (_gate) _failure ??= reason; }
    private void Eof(ProcessOutputSource source)
    {
        lock (_gate)
        {
            if (source == ProcessOutputSource.StandardOutput) _stdoutEof = true;
            else _stderrEof = true;
        }
    }
    private ProcessOutputDrain Snapshot(bool timedOut) => new(_stdoutEof, _stderrEof, _discarded, _failure, timedOut);
}
