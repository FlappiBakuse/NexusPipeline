using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusPipeline.Platform.Processes;

internal sealed record OwnedProcessExit(ProcessIdentity Identity, int? ExitCode);
internal sealed record OwnedProcessCompletion(bool Complete, uint TotalProcesses, OwnedProcessExit[] Processes);

internal sealed partial class ProcessOwnership
{
    private readonly Dictionary<ProcessIdentity, SafeProcessHandle> _exitHandles = new();

    private void RetainExitHandle(Process process, ProcessIdentity identity)
    {
        lock (_observationSync)
        {
            if (_disposed || _exitHandles.ContainsKey(identity) || _exitHandles.Count >= JobProcessIdReader.MaximumCapacity) return;
            if (!IsProcessInJob(process.Handle, _job.DangerousGetHandle(), out bool member) || !member) return;
            IntPtr self = GetCurrentProcess();
            if (DuplicateHandle(self, process.Handle, self, out SafeProcessHandle copy, 0, false, 2))
                _exitHandles.Add(identity, copy);
            else copy.Dispose();
        }
    }

    internal OwnedProcessCompletion ObserveCompletion()
    {
        _ = Observe();
        lock (_observationSync)
        {
            if (!IsUsable || !HasAssignedProcess
                || !QueryInformationJobObject(_job.DangerousGetHandle(), 1, out JobAccounting accounting,
                    Marshal.SizeOf<JobAccounting>(), out int returned) || returned != Marshal.SizeOf<JobAccounting>())
                return new(false, 0, []);
            var facts = new List<OwnedProcessExit>();
            bool complete = accounting.TotalProcesses == _exitHandles.Count && accounting.TotalProcesses > 0;
            foreach (var (identity, handle) in _exitHandles)
            {
                uint wait = WaitForSingleObject(handle, 0);
                int? code = null;
                if (wait == 0 && GetExitCodeProcess(handle, out uint exit)) code = unchecked((int)exit);
                else if (wait != 258) complete = false;
                facts.Add(new(identity, code));
            }
            // Retained handles identify exited processes even when PID lookup can no longer resolve them.
            // Job accounting also detects children that exited between samples.
            return new(complete, accounting.TotalProcesses, facts.ToArray());
        }
    }

    private void ReleaseExitHandles()
    {
        lock (_observationSync)
        {
            foreach (var handle in _exitHandles.Values) handle.Dispose();
            _exitHandles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeProcessHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out JobAccounting information, int size, out int returned);
}
