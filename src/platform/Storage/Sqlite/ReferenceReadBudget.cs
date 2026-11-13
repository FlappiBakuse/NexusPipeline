using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed class ReferenceReadDeferred : IOException { }

internal sealed class ReferenceReadBudget(TimeSpan limit, long maximumReadBytes = long.MaxValue)
{
    private TimeSpan _elapsed;
    private int _reads;
    private readonly long _initialReadBytes = ReadBytes();
    internal long ObservedReadBytes => Math.Max(0, ReadBytes() - _initialReadBytes);

    internal IDisposable Enter()
    {
        if (_reads > 0 && (_elapsed >= limit || ObservedReadBytes >= maximumReadBytes)) throw new ReferenceReadDeferred();
        _reads++;
        return new Measurement(this);
    }

    private static long ReadBytes()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (!GetProcessIoCounters(new IntPtr(-1), out var value))
            throw new IOException("reference_read_budget_unavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        return checked((long)value.ReadBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    private sealed class Measurement(ReferenceReadBudget owner) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        public void Dispose() => owner._elapsed += Stopwatch.GetElapsedTime(_started);
    }
}
