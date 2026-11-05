using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class ConsoleStopFixture
{
    internal static async Task<int> RunAsync(string id, string image, string created)
    {
        OwnedFixtureEvidence.RequirePath(image);
        using var host = Process.GetProcessById(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
        if (host.StartTime.ToUniversalTime().Ticks != long.Parse(created, System.Globalization.CultureInfo.InvariantCulture) ||
            !StringComparer.OrdinalIgnoreCase.Equals(host.MainModule!.FileName, image)) return 81;
        _ = FreeConsole();
        if (!AttachConsole((uint)host.Id)) return 82;
        try
        {
            var processes = new uint[16];
            var count = ConsoleProcesses(processes, (uint)processes.Length);
            if (count != 2 || !processes.Take((int)count).ToHashSet().SetEquals([(uint)host.Id, (uint)Environment.ProcessId])) return 83;
            if (!ConsoleHandler(IntPtr.Zero, true) || !SignalConsole(0, 0)) return 84;
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            return host.ExitCode;
        }
        finally { _ = FreeConsole(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetConsoleProcessList", SetLastError = true)] private static extern uint ConsoleProcesses(uint[] processes, uint length);
    [DllImport("kernel32.dll", EntryPoint = "SetConsoleCtrlHandler", SetLastError = true)] private static extern bool ConsoleHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", EntryPoint = "GenerateConsoleCtrlEvent", SetLastError = true)] private static extern bool SignalConsole(uint controlEvent, uint processGroup);
}
