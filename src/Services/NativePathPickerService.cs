using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NexusPipeline.Services;

/// <summary>在专用 STA 线程上承载 Windows 原生文件/文件夹选择器，避免阻塞 Web 请求线程。</summary>
internal sealed class NativePathPickerService : IDisposable
{
    private sealed record WorkItem(NativePathPickerRequest Request, TaskCompletionSource<string?> Completion);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    private sealed class WindowHandle(nint handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public NativePathPickerService()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "NexusPipeline Native Path Picker",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<string?> PickAsync(NativePathPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(NativePathPickerService));
        }

        if (request.OwnerHandle == 0)
        {
            request = request with { OwnerHandle = CaptureForegroundWindow() };
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(new WorkItem(request, completion));
        }
        catch (InvalidOperationException)
        {
            completion.SetException(new ObjectDisposedException(nameof(NativePathPickerService)));
        }
        return completion.Task;
    }

    internal static bool IsExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Directory.Exists(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string ResolveInitialDirectory(string? initialPath)
    {
        string fallback = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(fallback) || !Directory.Exists(fallback))
        {
            fallback = AppContext.BaseDirectory;
        }

        string value = initialPath?.Trim() ?? "";
        if (value.Length == 0)
        {
            return fallback;
        }
        try
        {
            if (Directory.Exists(value))
            {
                return Path.GetFullPath(value);
            }
            if (File.Exists(value))
            {
                return Path.GetDirectoryName(Path.GetFullPath(value)) ?? fallback;
            }

            string? directory = Path.GetDirectoryName(value);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return Path.GetFullPath(directory);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // 非法或已不存在的输入只影响初始位置，仍使用安全默认目录打开选择器。
        }
        return fallback;
    }

    private void Run()
    {
        foreach (WorkItem item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item.Completion.TrySetResult(Pick(item.Request));
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }

    private static string? Pick(NativePathPickerRequest request)
    {
        nint ownerHandle = ResolveOwnerHandle(request);
        request = request with { OwnerHandle = ownerHandle };
        ActivateOwner(ownerHandle);
        IWin32Window? owner = ownerHandle == 0 ? null : new WindowHandle(ownerHandle);
        return request.Kind switch
        {
            "folder" => PickFolder(request, owner),
            _ => PickFile(request, owner),
        };
    }

    private static string? PickFile(NativePathPickerRequest request, IWin32Window? owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = request.Title,
            Filter = string.IsNullOrWhiteSpace(request.Filter) ? "所有文件|*.*" : request.Filter,
            CheckFileExists = false,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            InitialDirectory = ResolveInitialDirectory(request.InitialPath),
        };
        string initialFileName = GetInitialFileName(request.InitialPath);
        if (initialFileName.Length > 0)
        {
            dialog.FileName = initialFileName;
        }
        return ShowDialog(dialog, owner) == DialogResult.OK
            ? dialog.FileName.Trim()
            : null;
    }

    private static string? PickFolder(NativePathPickerRequest request, IWin32Window? owner)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = request.Title,
            UseDescriptionForTitle = true,
            SelectedPath = ResolveInitialDirectory(request.InitialPath),
            ShowNewFolderButton = true,
        };
        return ShowDialog(dialog, owner) == DialogResult.OK
            ? dialog.SelectedPath.Trim()
            : null;
    }

    private static DialogResult ShowDialog(CommonDialog dialog, IWin32Window? owner)
    {
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    private static DialogResult ShowDialog(Form dialog, IWin32Window? owner)
    {
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    private static nint ResolveOwnerHandle(NativePathPickerRequest request)
    {
        if (request.OwnerHandle != 0 && IsWindow(request.OwnerHandle)) return request.OwnerHandle;
        nint foreground = CaptureForegroundWindow();
        return foreground != 0 && IsWindow(foreground) ? foreground : 0;
    }

    private static nint CaptureForegroundWindow()
    {
        nint foreground = GetForegroundWindow();
        return foreground != 0 && IsWindow(foreground) ? foreground : 0;
    }

    private static void ActivateOwner(nint ownerHandle)
    {
        if (ownerHandle != 0 && IsWindow(ownerHandle)) SetForegroundWindow(ownerHandle);
    }

    private static string GetInitialFileName(string? initialPath)
    {
        string value = initialPath?.Trim() ?? "";
        if (value.Length == 0 || Directory.Exists(value)) return "";
        try
        {
            string name = Path.GetFileName(value);
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "" : name;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
    }
}

internal sealed record NativePathPickerRequest(
    string Kind,
    string Title,
    string? InitialPath,
    string? Filter,
    nint OwnerHandle = 0);
