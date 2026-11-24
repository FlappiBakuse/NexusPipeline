using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ConsoleHostFixture : IDisposable
{
    private readonly AnonymousPipeServerStream _output = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _error = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _input = new(PipeDirection.Out, HandleInheritability.Inheritable);
    internal Process Process { get; }
    internal StreamReader Output { get; }
    internal Task<string> Errors { get; }
    private readonly StreamReader _errors;

    internal ConsoleHostFixture(string executable, string runtime, int port, Action<int>? beforeAdoption = null)
    {
        var handles = new[] { _input.ClientSafePipeHandle.DangerousGetHandle(), _output.ClientSafePipeHandle.DangerousGetHandle(),
            _error.ClientSafePipeHandle.DangerousGetHandle() };
        using var attributes = new ProcessHandleAttributes(handles);
        var startup = new JobNative.ExtendedStartupInfo
        {
            Startup = new() { Size = (uint)Marshal.SizeOf<JobNative.ExtendedStartupInfo>(), Flags = 0x101,
                ShowWindow = 0, Input = handles[0], Output = handles[1], Error = handles[2] },
            Attributes = attributes.Pointer
        };
        var command = new StringBuilder('"' + executable + "\" serve --runtime-root \"" + runtime + "\" --port " +
            port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A separate hidden console makes a real interruption signal exclusive to this Host.
        if (!JobNative.CreateProcessExtended(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x80010,
            IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var native))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var nativeProcess = new SafeKernelHandle(native.Process);
        using var nativeThread = new SafeKernelHandle(native.Thread);
        Process? process = null;
        StreamReader? output = null;
        StreamReader? errors = null;
        try
        {
            beforeAdoption?.Invoke((int)native.ProcessId);
            process = Process.GetProcessById((int)native.ProcessId);
            _ = process.SafeHandle;
            _output.DisposeLocalCopyOfClientHandle();
            _error.DisposeLocalCopyOfClientHandle();
            _input.DisposeLocalCopyOfClientHandle();
            output = new(_output);
            errors = new(_error);
            Errors = errors.ReadToEndAsync();
            Process = process;
            Output = output;
            _errors = errors;
        }
        catch (Exception failure)
        {
            if (JobNative.WaitForSingleObject(nativeProcess, 0) != 0 &&
                (!JobNative.TerminateProcess(nativeProcess, 1) && JobNative.WaitForSingleObject(nativeProcess, 0) != 0 ||
                    JobNative.WaitForSingleObject(nativeProcess, 5000) != 0))
                failure.Data["stop-console-host"] = "console_host_construction_stop_failed";
            process?.Dispose();
            output?.Dispose(); errors?.Dispose();
            _output.Dispose(); _error.Dispose(); _input.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Output.Dispose(); _errors.Dispose(); _output.Dispose(); _error.Dispose(); _input.Dispose();
    }
}
