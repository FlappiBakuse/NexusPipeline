using System.Runtime.InteropServices;

namespace NexusPipeline.Maintenance;

internal static class UpdateDialogNative
{
    internal const uint Created = 0, Clicked = 2, Timer = 4;
    private const uint NavigatePage = 1125, ClickButton = 1126, SetProgress = 1130, SetContent = 1132, EnableButton = 1135;
    internal delegate int Notify(nint window, uint notification, nint parameter, nint unused, nint data);
    internal sealed record Page(string Instruction, string Content, Button Check, Button Primary, Button Close);

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    internal readonly struct Button(int id, string text)
    {
        public readonly int Id = id;
        public readonly string Text = text;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct Dialog
    {
        internal uint Size;
        internal nint Parent, Instance;
        internal uint Flags, CommonButtons;
        internal string Title;
        internal nint MainIcon;
        internal string Instruction, Content;
        internal uint ButtonCount;
        internal nint Buttons;
        internal int DefaultButton;
        internal uint RadioCount;
        internal nint RadioButtons;
        internal int DefaultRadio;
        internal nint Verification, Expanded, ExpandedControl, CollapsedControl, FooterIcon, Footer;
        internal Notify Callback;
        internal nint Data;
        internal uint Width;
    }

    internal static void Show(Page page, Notify callback) => UsePage(page, callback, config =>
        Marshal.ThrowExceptionForHR(TaskDialogIndirect(ref config, out _, IntPtr.Zero, IntPtr.Zero)));

    internal static void Change(nint window, Page page, Notify callback) => UsePage(page, callback, config =>
    {
        var address = Marshal.AllocHGlobal(Marshal.SizeOf<Dialog>());
        try
        {
            Marshal.StructureToPtr(config, address, false);
            try { SendMessageW(window, NavigatePage, 0, address); }
            finally { Marshal.DestroyStructure<Dialog>(address); }
        }
        finally { Marshal.FreeHGlobal(address); }
    });

    private static void UsePage(Page page, Notify callback, Action<Dialog> show)
    {
        var size = Marshal.SizeOf<Button>();
        var buttons = Marshal.AllocHGlobal(size * 3);
        Marshal.StructureToPtr(page.Check, buttons, false);
        Marshal.StructureToPtr(page.Primary, buttons + size, false);
        Marshal.StructureToPtr(page.Close, buttons + size * 2, false);
        try
        {
            var config = new Dialog
            {
                Size = (uint)Marshal.SizeOf<Dialog>(), Title = "NexusPipeline.update", Instruction = page.Instruction, Content = page.Content,
                Flags = 0x8 | 0x200 | 0x800, ButtonCount = 3, Buttons = buttons, DefaultButton = page.Check.Id,
                Callback = callback, Width = 310
            };
            show(config);
        }
        finally
        {
            Marshal.DestroyStructure<Button>(buttons);
            Marshal.DestroyStructure<Button>(buttons + size);
            Marshal.DestroyStructure<Button>(buttons + size * 2);
            Marshal.FreeHGlobal(buttons);
            GC.KeepAlive(callback);
        }
    }

    internal static void Content(nint window, string text) => SendText(window, SetContent, 0, text);
    internal static void Progress(nint window, int percent) => SendMessageW(window, SetProgress, percent, 0);
    internal static void Enable(nint window, int id, bool enabled) => SendMessageW(window, EnableButton, id, enabled ? 1 : 0);
    internal static void Close(nint window, int id) => SendMessageW(window, ClickButton, id, 0);
    internal static void Error(string message) => MessageBoxW(0, message, "NexusPipeline.update", 0x10);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref Dialog config, out int pressed, nint radio, nint verified);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint first, nint second);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint SendText(nint window, uint message, nint first, string text);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string message, string title, uint flags);
}
