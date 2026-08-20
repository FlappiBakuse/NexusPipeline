namespace NexusPipeline;

/// <summary>进程入口仅负责转交给应用宿主，便于将命令分发与生命周期逻辑从平台入口隔离。</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return ApplicationHost.Run(args);
    }
}
