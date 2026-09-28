using System.Runtime.InteropServices;

namespace NexusPipeline.Platform.Processes;

internal enum ProcessObservationQuality
{
    Complete,
    Partial,
    Unavailable,
}

/// <summary>Job 的一次观测；空身份集合只有在 Complete 且原始 PID 也为空时才证明空。</summary>
internal sealed record ProcessObservation(
    ProcessObservationQuality Quality,
    IReadOnlyList<int> RawPids,
    IReadOnlyList<ProcessIdentity> Identities,
    IReadOnlyList<int> UnresolvedPids,
    IReadOnlyList<int> ExitedDuringCapturePids,
    DateTimeOffset ObservedAt,
    int? NativeErrorCode)
{
    public bool IsComplete => Quality == ProcessObservationQuality.Complete;

    public bool IsTrustworthyEmpty => IsComplete && RawPids.Count == 0 && Identities.Count == 0;
}

internal readonly record struct JobQueryAttempt(bool Success, int ErrorCode, int ReturnLength);

internal sealed record JobPidQueryResult(bool Complete, IReadOnlyList<int> Pids, int? ErrorCode);

/// <summary>有界扩容 Job PID 缓冲；原生失败不产生成功的空列表。</summary>
internal static class JobProcessIdReader
{
    internal const int InitialCapacity = 64;
    internal const int MaximumCapacity = 16384;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    internal static JobPidQueryResult Read(Func<IntPtr, int, JobQueryAttempt> query)
    {
        int capacity = InitialCapacity;
        while (capacity <= MaximumCapacity)
        {
            int bufferSize = checked(8 + IntPtr.Size * capacity);
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                // A failing native query need not initialize its output buffer.
                Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
                JobQueryAttempt attempt = query(buffer, bufferSize);
                if (!attempt.Success)
                {
                    if (attempt.ErrorCode is not (ErrorInsufficientBuffer or ErrorMoreData))
                        return new(false, Array.Empty<int>(), attempt.ErrorCode);
                    int requiredSlots = attempt.ReturnLength > 8
                        ? (attempt.ReturnLength - 8 + IntPtr.Size - 1) / IntPtr.Size
                        : 0;
                    int next = Math.Max(capacity + 1, Math.Max(capacity * 2, requiredSlots));
                    if (next > MaximumCapacity)
                        return new(false, Array.Empty<int>(), attempt.ErrorCode);
                    capacity = next;
                    continue;
                }

                uint assigned = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                uint listed = unchecked((uint)Marshal.ReadInt32(buffer, 4));
                if (listed > capacity || assigned > listed)
                {
                    long next = Math.Max((long)capacity * 2, Math.Max((long)assigned, (long)listed));
                    if (next > MaximumCapacity)
                        return new(false, Array.Empty<int>(), ErrorMoreData);
                    capacity = (int)next;
                    continue;
                }
                var pids = new List<int>((int)listed);
                for (int i = 0; i < listed; i++)
                {
                    long value = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64();
                    if (value <= 0 || value > int.MaxValue)
                        return new(false, Array.Empty<int>(), null);
                    pids.Add((int)value);
                }
                return new(true, pids, null);
            }
            catch (Exception) when (capacity > 0)
            {
                return new(false, Array.Empty<int>(), null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new(false, Array.Empty<int>(), ErrorMoreData);
    }
}
