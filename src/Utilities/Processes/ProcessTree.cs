using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusPipeline.Utilities;

internal static class ProcessTree
{
    internal sealed record ProcessNode(int Pid, int Ppid, string ExeName);

    /// <summary>Toolhelp 快照全部进程（PID/父 PID/映像名）；失败抛异常由调用方回退。</summary>
    internal static IReadOnlyDictionary<int, ProcessNode> SnapshotProcesses()
    {
        var nodes = new Dictionary<int, ProcessNode>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            throw new InvalidOperationException($"进程快照失败（错误码 {Marshal.GetLastWin32Error()}）");
        }
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    nodes[(int)entry.th32ProcessID] = new ProcessNode((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile);
                }
                while (Process32Next(snapshot, ref entry));
            }
            return nodes;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>从根 PID BFS 收集进程树；excludeBaseName 匹配的节点跳过且不扩展其子树（internal 供单元测试验证纯逻辑）。</summary>
    internal static HashSet<int> CollectTree(int rootPid, IReadOnlyDictionary<int, ProcessNode> nodes, string? excludeBaseName)
    {
        var result = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (result.Contains(pid) || !nodes.TryGetValue(pid, out ProcessNode? node))
            {
                continue;
            }
            // 根 PID 是本次宿主启动得到的 owned root，即便与 GameExe 同名也必须纳入脚本清理；
            // 只有根的后代匹配游戏身份时才排除该分支。
            if (pid != rootPid && excludeBaseName is not null && IsSameProcessName(node.ExeName, excludeBaseName))
            {
                continue;
            }
            result.Add(pid);
            foreach ((int childPid, ProcessNode child) in nodes)
            {
                if (child.Ppid == pid)
                {
                    queue.Enqueue(childPid);
                }
            }
        }
        return result;
    }

    /// <summary>按稳定的 BFS 顺序返回根进程及其后代 PID，根进程优先，兄弟进程按 PID 排序。</summary>
    internal static IReadOnlyList<int> ProcessTreeOrder(
        int rootPid,
        IReadOnlyDictionary<int, ProcessNode> nodes)
    {
        var childrenByParent = nodes.Values
            .GroupBy(node => node.Ppid)
            .ToDictionary(
                group => group.Key,
                group => group.Select(node => node.Pid).OrderBy(pid => pid).ToArray());
        var result = new List<int>();
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (!visited.Add(pid) || !nodes.ContainsKey(pid))
            {
                continue;
            }
            result.Add(pid);
            if (childrenByParent.TryGetValue(pid, out int[]? children))
            {
                foreach (int childPid in children)
                {
                    queue.Enqueue(childPid);
                }
            }
        }
        return result;
    }

    internal static ProcessIdentity? CaptureProcessIdentity(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            return ProcessIdentity.Capture(process);
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSameProcessName(string exeFile, string baseName)
    {
        try
        {
            return string.Equals(Path.GetFileNameWithoutExtension(exeFile), baseName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn($"进程名比对失败，按非游戏进程处理（{exeFile}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>单个进程强制结束（taskkill /F，不带 /T）。</summary>
    internal static bool KillProcess(int pid)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(10000);
            if (process is not null && process.ExitCode == 0)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] taskkill 清理 PID {pid} 失败：{ex.Message}，尝试 Process.Kill。");
        }
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return true;
            }
            process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;

        public uint cntUsage;

        public uint th32ProcessID;

        public IntPtr th32DefaultHeapID;

        public uint th32ModuleID;

        public uint cntThreads;

        public uint th32ParentProcessID;

        public int pcPriClassBase;

        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
