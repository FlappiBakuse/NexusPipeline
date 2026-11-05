using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NexusPipeline.Platform.Processes;

[SupportedOSPlatform("windows")]
public sealed class OwnedProcessOutput : IDisposable
{
    private readonly object _gate = new();
    private readonly AnonymousPipeServerStream _output = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _error;
    private readonly AnonymousPipeServerStream _input;
    private readonly bool _redirectInput;
    private bool _started;
    private bool _disposed;
    public Stream StandardOutput => _output;
    public Stream StandardError => _error;
    internal Stream StandardInput => _redirectInput ? _input : throw new InvalidOperationException("worker_input_not_redirected");

    public OwnedProcessOutput() : this(false) { }

    internal OwnedProcessOutput(bool redirectInput)
    {
        _redirectInput = redirectInput;
        try
        {
            _error = new(PipeDirection.In, HandleInheritability.Inheritable);
            try { _input = new(PipeDirection.Out, HandleInheritability.Inheritable); }
            catch { _error.Dispose(); throw; }
        }
        catch { _output.Dispose(); throw; }
    }

    internal bool Create(string image, StringBuilder commandLine, string directory, out JobNative.ProcessInformation process,
        SafeKernelHandle? maintenanceStartupJob = null, IntPtr frozenEnvironment = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("worker_output_already_bound");
            _started = true;
            try
            {
                using var attributes = new ProcessHandleAttributes([
                    _input.ClientSafePipeHandle.DangerousGetHandle(), _output.ClientSafePipeHandle.DangerousGetHandle(),
                    _error.ClientSafePipeHandle.DangerousGetHandle()], maintenanceStartupJob);
                var startup = new JobNative.ExtendedStartupInfo
                {
                    Startup = new() { Size = (uint)Marshal.SizeOf<JobNative.ExtendedStartupInfo>(), Flags = 0x100,
                        Input = _input.ClientSafePipeHandle.DangerousGetHandle(), Output = _output.ClientSafePipeHandle.DangerousGetHandle(),
                        Error = _error.ClientSafePipeHandle.DangerousGetHandle() },
                    Attributes = attributes.Pointer
                };
                // Maintenance has explicit pipes and must not add a console host to its startup family.
                var flags = (maintenanceStartupJob is null ? 0x08080004U : 0x0108000CU) |
                    (frozenEnvironment == IntPtr.Zero ? 0U : 0x400U);
                if (!JobNative.CreateProcessExtended(image, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags,
                    frozenEnvironment, directory, ref startup, out process)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return true;
            }
            finally
            {
                _output.DisposeLocalCopyOfClientHandle(); _error.DisposeLocalCopyOfClientHandle();
                _input.DisposeLocalCopyOfClientHandle();
                if (!_redirectInput) _input.Dispose();
            }
        }
    }

    internal void CompleteInput()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_redirectInput || !_started) throw new InvalidOperationException("worker_input_not_redirected");
            _input.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _output.Dispose(); _error.Dispose(); _input.Dispose();
        }
    }

    internal void CloseUnstartedEnds()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _output.DisposeLocalCopyOfClientHandle(); _error.DisposeLocalCopyOfClientHandle();
            _input.DisposeLocalCopyOfClientHandle(); _input.Dispose();
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ProcessHandleAttributes : IDisposable
{
    internal IntPtr Pointer { get; private set; }
    private IntPtr _handles, _jobList;
    private SafeKernelHandle? _job;
    private bool _jobRetained, _initialized;

    internal ProcessHandleAttributes(IntPtr[] handles, SafeKernelHandle? maintenanceStartupJob = null)
    {
        try
        {
            if (handles.Length == 0 && maintenanceStartupJob is null) throw new ArgumentException("worker_attributes_empty");
            var count = (handles.Length == 0 ? 0 : 1) + (maintenanceStartupJob is null ? 0 : 1);
            UIntPtr size = 0;
            JobNative.InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref size);
            if (size == 0 || size > 65536) throw new IOException("worker_handle_attributes_invalid");
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            if (!JobNative.InitializeProcThreadAttributeList(Pointer, count, 0, ref size)) throw Failure();
            _initialized = true;
            if (handles.Length != 0)
            {
                _handles = Marshal.AllocHGlobal(checked(handles.Length * IntPtr.Size));
                Marshal.Copy(handles, 0, _handles, handles.Length);
                // An explicit list prevents unrelated inheritable handles from crossing the launch boundary.
                if (!JobNative.UpdateProcThreadAttribute(Pointer, 0, 0x00020002, _handles,
                    (UIntPtr)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw Failure();
            }
            if (maintenanceStartupJob is not null)
            {
                _job = maintenanceStartupJob;
                _job.DangerousAddRef(ref _jobRetained);
                if (_job.IsInvalid || _job.IsClosed) throw new IOException("maintenance_startup_job_closed");
                _jobList = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_jobList, _job.DangerousGetHandle());
                if (!JobNative.UpdateProcThreadAttribute(Pointer, 0, 0x0002000D, _jobList,
                    (UIntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw Failure();
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_initialized) { JobNative.DeleteProcThreadAttributeList(Pointer); _initialized = false; }
        if (_jobList != IntPtr.Zero) { Marshal.FreeHGlobal(_jobList); _jobList = IntPtr.Zero; }
        if (_jobRetained) { _job!.DangerousRelease(); _jobRetained = false; }
        if (_handles != IntPtr.Zero) { Marshal.FreeHGlobal(_handles); _handles = IntPtr.Zero; }
        if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
}
