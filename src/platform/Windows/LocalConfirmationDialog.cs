using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NexusPipeline.Platform.Windows;

internal sealed record LocalConfirmationPrompt(Guid ChallengeId, string Title, string Details, string ApproveLabel);

internal interface ILocalConfirmationDialog
{
    Task<bool> ShowAsync(LocalConfirmationPrompt prompt, TimeSpan timeout, CancellationToken cancellation);
}

[SupportedOSPlatform("windows")]
internal sealed class LocalConfirmationDialog : ILocalConfirmationDialog
{
    public Task<bool> ShowAsync(LocalConfirmationPrompt prompt, TimeSpan timeout, CancellationToken cancellation)
    {
        if (prompt.ChallengeId == Guid.Empty || prompt.Title.Length is < 1 or > 120 ||
            prompt.Details.Length is < 1 or > 32768 || prompt.ApproveLabel.Length is < 1 or > 40 ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentException("completion_confirmation_request_invalid");
        if (!Environment.UserInteractive || cancellation.IsCancellationRequested) return Task.FromResult(false);
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.TrySetResult(Show(prompt, timeout, cancellation)); }
            catch (Win32Exception) { result.TrySetResult(false); }
            catch (Exception error) { result.TrySetException(error); }
        }) { IsBackground = true, Name = "Nexus completion confirmation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return result.Task;
    }

    private static bool Show(LocalConfirmationPrompt prompt, TimeSpan timeout, CancellationToken cancellation)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var title = prompt.Title + " " + prompt.ChallengeId.ToString("N");
        var body = prompt.Details;
        var template = Template(title, string.Empty, prompt.ApproveLabel);
        var memory = Marshal.AllocHGlobal(template.Length);
        DialogProcedure procedure = Handle;
        try
        {
            Marshal.Copy(template, 0, memory, template.Length);
            var response = DialogBoxIndirectParamW(IntPtr.Zero, memory, IntPtr.Zero, procedure, IntPtr.Zero);
            if (response == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return response == new IntPtr(1) && !cancellation.IsCancellationRequested &&
                System.Diagnostics.Stopwatch.GetElapsedTime(started) < timeout;
        }
        finally { GC.KeepAlive(procedure); Marshal.FreeHGlobal(memory); }

        IntPtr Handle(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
        {
            if (message == 0x110)
            {
                var details = GetDlgItem(window, 100);
                SendMessageW(details, 0xc5, new UIntPtr(4_000_000), IntPtr.Zero);
                if (!SetWindowTextW(details, body)) { EndDialog(window, new IntPtr(2)); return new(1); }
                if (SetTimer(window, new UIntPtr(1), 100, IntPtr.Zero) == UIntPtr.Zero) { EndDialog(window, new IntPtr(2)); return new(1); }
                SetFocus(GetDlgItem(window, 2)); return IntPtr.Zero;
            }
            if (message == 0x113 && (cancellation.IsCancellationRequested ||
                System.Diagnostics.Stopwatch.GetElapsedTime(started) >= timeout) || message == 0x10)
            { EndDialog(window, 2); return new(1); }
            if (message == 0x111)
            {
                var id = unchecked((ushort)wParam.ToUInt64());
                if (id is 1 or 2)
                {
                    var allowed = id == 1 && !cancellation.IsCancellationRequested &&
                        System.Diagnostics.Stopwatch.GetElapsedTime(started) < timeout;
                    EndDialog(window, new IntPtr(allowed ? 1 : 2)); return new(1);
                }
            }
            return IntPtr.Zero;
        }
    }

    private static byte[] Template(string title, string body, string approveLabel)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.Unicode, leaveOpen: true);
        writer.Write(0x80c80080u); writer.Write(0u); writer.Write((ushort)3);
        Rect(0, 0, 360, 210); writer.Write((ushort)0); writer.Write((ushort)0); Text(title);
        Item(0x50a10844u, 10, 10, 340, 158, 100, 0x81, body);
        Item(0x50010000u, 174, 184, 80, 16, 1, 0x80, approveLabel);
        Item(0x50010001u, 264, 184, 80, 16, 2, 0x80, "Cancel");
        return memory.ToArray();
        void Rect(short x, short y, short width, short height)
        { writer.Write(x); writer.Write(y); writer.Write(width); writer.Write(height); }
        void Text(string value) { foreach (var character in value) writer.Write((ushort)character); writer.Write((ushort)0); }
        void Align() { while (memory.Position % 4 != 0) writer.Write((byte)0); }
        void Item(uint style, short x, short y, short width, short height, ushort id, ushort kind, string value)
        {
            Align(); writer.Write(style); writer.Write(0u); Rect(x, y, width, height); writer.Write(id);
            writer.Write((ushort)0xffff); writer.Write(kind); Text(value); writer.Write((ushort)0);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr DialogProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr DialogBoxIndirectParamW(IntPtr instance, IntPtr template, IntPtr parent, DialogProcedure procedure, IntPtr data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDialog(IntPtr window, IntPtr result);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr window, string text);
}
