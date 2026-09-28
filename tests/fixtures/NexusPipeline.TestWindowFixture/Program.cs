using System.Diagnostics;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--owned-window"
            || !int.TryParse(args[1], out int delay) || delay is < 0 or > 10000
            || !File.Exists(".nxp-test-fixture")
            || File.ReadAllText(".nxp-test-fixture") != "owned-process-contract") return 4;

        string root = Environment.CurrentDirectory;
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < delay)
        {
            if (File.Exists(Path.Combine(root, "stop-window"))) return 0;
            Thread.Sleep(20);
        }

        using var window = new Form { Text = "Nexus owned readiness fixture", Width = 300, Height = 200 };
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => { if (File.Exists(Path.Combine(root, "stop-window"))) window.Close(); };
        window.Shown += (_, _) =>
        {
            string marker = Path.Combine(root, "window-ready");
            File.WriteAllText(marker + ".tmp", "owned-window");
            File.Move(marker + ".tmp", marker);
            timer.Start();
        };
        Application.Run(window);
        return 0;
    }
}
