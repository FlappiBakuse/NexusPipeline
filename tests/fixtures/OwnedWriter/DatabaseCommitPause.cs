using System.Runtime.InteropServices;

internal sealed class DatabaseCommitPause : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vfs
    {
        internal int Version, FileSize, PathSize;
        internal nint Next, Name, Data, Open, Delete, Access, FullPath, Load, LoadError, Symbol, Unload;
        internal nint Random, Sleep, Time, Error, Time64, SetCall, GetCall, NextCall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Methods
    {
        internal int Version;
        internal nint Close, Read, Write, Truncate, Sync, FileSize, Lock, Unlock, Reserved, Control, Sector, Characteristics;
        internal nint ShmMap, ShmLock, ShmBarrier, ShmUnmap, Fetch, Unfetch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Find(nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Register(nint vfs, int makeDefault);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Unregister(nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(nint vfs, nint name, nint file, int flags, nint actual);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Sync(nint file, int flags);

    private sealed record FileMethods(nint Original, nint Wrapped, Close Close, Sync Sync, bool System);
    private readonly object _gate = new();
    private readonly Dictionary<nint, FileMethods> _files = [];
    private readonly nint _library, _original, _vfs, _name;
    private readonly Open _open, _openCallback;
    private readonly Close _closeCallback;
    private readonly Sync _syncCallback;
    private readonly string _state;
    private readonly Action _checkpoint;
    private int _armed;

    internal DatabaseCommitPause(string root, Action checkpoint)
    {
        _state = Path.Combine(root, "state"); _checkpoint = checkpoint;
        var library = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        if (!File.Exists(library)) library = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll");
        _library = NativeLibrary.Load(library);
        _original = Marshal.GetDelegateForFunctionPointer<Find>(NativeLibrary.GetExport(_library, "sqlite3_vfs_find"))(0);
        if (_original == 0) throw new IOException("database_native_vfs_missing");
        var copy = Marshal.PtrToStructure<Vfs>(_original);
        _open = Marshal.GetDelegateForFunctionPointer<Open>(copy.Open);
        _openCallback = OpenFile; _closeCallback = CloseFile; _syncCallback = SyncFile;
        _name = Marshal.StringToCoTaskMemUTF8("nexus-database-commit-pause");
        copy.Name = _name; copy.Next = 0; copy.Open = Marshal.GetFunctionPointerForDelegate(_openCallback);
        _vfs = Marshal.AllocHGlobal(Marshal.SizeOf<Vfs>()); Marshal.StructureToPtr(copy, _vfs, false);
        if (Marshal.GetDelegateForFunctionPointer<Register>(NativeLibrary.GetExport(_library, "sqlite3_vfs_register"))(_vfs, 1) != 0)
            throw new IOException("database_native_vfs_rejected");
    }

    internal void Arm() => Volatile.Write(ref _armed, 1);

    private int OpenFile(nint ignored, nint name, nint file, int flags, nint actual)
    {
        var result = _open(_original, name, file, flags, actual);
        if (result != 0 || Marshal.ReadIntPtr(file) == 0) return result;
        var original = Marshal.ReadIntPtr(file);
        var methods = Marshal.PtrToStructure<Methods>(original);
        var path = Marshal.PtrToStringUTF8(name)?.Replace('/', '\\');
        var system = (flags & 0x100) != 0 && path?.EndsWith("\\state\\system.db", StringComparison.OrdinalIgnoreCase) == true;
        var close = Marshal.GetDelegateForFunctionPointer<Close>(methods.Close);
        var sync = Marshal.GetDelegateForFunctionPointer<Sync>(methods.Sync);
        methods.Close = Marshal.GetFunctionPointerForDelegate(_closeCallback);
        methods.Sync = Marshal.GetFunctionPointerForDelegate(_syncCallback);
        var wrapped = Marshal.AllocHGlobal(Marshal.SizeOf<Methods>()); Marshal.StructureToPtr(methods, wrapped, false);
        lock (_gate) _files.Add(file, new(original, wrapped, close, sync, system));
        Marshal.WriteIntPtr(file, wrapped); return result;
    }

    private int SyncFile(nint file, int flags)
    {
        FileMethods methods; lock (_gate) methods = _files[file];
        if (methods.System && Volatile.Read(ref _armed) != 0 &&
            Directory.EnumerateFiles(_state, "runtime.db-mj*").Any(path => new FileInfo(path).Length > 0) &&
            Interlocked.Exchange(ref _armed, 0) != 0)
        {
            // Native pages have been written; the super-journal still makes the whole save uncommitted.
            _checkpoint();
            using var waiting = new ManualResetEvent(false);
            if (!waiting.WaitOne(TimeSpan.FromSeconds(30))) return 10;
        }
        return methods.Sync(file, flags);
    }

    private int CloseFile(nint file)
    {
        FileMethods methods;
        lock (_gate) { methods = _files[file]; _files.Remove(file); }
        Marshal.WriteIntPtr(file, methods.Original);
        var result = methods.Close(file); Marshal.FreeHGlobal(methods.Wrapped); return result;
    }

    public void Dispose()
    {
        var register = Marshal.GetDelegateForFunctionPointer<Register>(NativeLibrary.GetExport(_library, "sqlite3_vfs_register"));
        _ = register(_original, 1);
        _ = Marshal.GetDelegateForFunctionPointer<Unregister>(NativeLibrary.GetExport(_library, "sqlite3_vfs_unregister"))(_vfs);
        lock (_gate)
        {
            foreach (var (file, methods) in _files)
            { Marshal.WriteIntPtr(file, methods.Original); Marshal.FreeHGlobal(methods.Wrapped); }
            _files.Clear();
        }
        Marshal.FreeHGlobal(_vfs); Marshal.FreeCoTaskMem(_name); NativeLibrary.Free(_library);
    }
}
