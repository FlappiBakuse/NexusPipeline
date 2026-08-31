namespace NexusPipeline.Persistence;

/// <summary>
/// 负责 service-owned 运行状态的当前目录布局。
/// </summary>
internal sealed class RuntimeStateLayout
{
    public RuntimeStateLayout(string appRoot)
    {
        AppRoot = Path.GetFullPath(appRoot);
        InternalDir = Path.Combine(AppRoot, ".nxp");
        RuntimeDir = Path.Combine(InternalDir, "runtime");
        StateDir = Path.Combine(InternalDir, "state");
        ServicePidPath = Path.Combine(RuntimeDir, "service.pid");
        WebPortPath = Path.Combine(RuntimeDir, "web.port");
        SchedulerStatePath = Path.Combine(StateDir, "scheduler-state.json");
    }

    public string AppRoot { get; }

    public string InternalDir { get; }

    public string RuntimeDir { get; }

    public string StateDir { get; }

    public string ServicePidPath { get; }

    public string WebPortPath { get; }

    public string SchedulerStatePath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(StateDir);
    }

}
