namespace NexusPipeline.Platform.Processes;

internal interface IOutputByteSink
{
    bool TryWrite(ProcessOutputSource source, ReadOnlySpan<byte> bytes);
    void Complete(ProcessOutputSource source, bool reachedEof);
}
