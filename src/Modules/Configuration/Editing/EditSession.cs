using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Modules.Configuration.Editing;

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ResolvedScriptUser User { get; init; }

    /// <summary>编辑开始时冻结的有效 profile；提交校验沿用同一版本，避免与当前插件重新加载的 validator 混用。</summary>
    public ResolvedScriptSpec? Spec { get; init; }

    /// <summary>编辑会话持有的脚本配置门禁租约；会话结束后才释放。</summary>
    public ScriptConfigGate.Lease? ConfigGate { get; set; }

    public Process? Process { get; set; }

    /// <summary>编辑进程启动瞬间捕获的 PID、启动时间和完整映像身份。</summary>
    public ProcessIdentity? ProcessIdentity { get; set; }

    /// <summary>编辑进程的 Job Object；只有成功分配根进程时才用于快速收尾。</summary>
    public ProcessOwnership? ProcessOwnership { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();

    public void DisposeProcessResources()
    {
        try
        {
            ProcessOwnership?.Dispose();
            ProcessOwnership = null;
            Process?.Dispose();
            Process = null;
        }
        finally
        {
            ReleaseConfigGate();
        }
    }

    private void ReleaseConfigGate()
    {
        ScriptConfigGate.Lease? gate = ConfigGate;
        ConfigGate = null;
        if (gate is null)
        {
            return;
        }

        try
        {
            gate.Release();
        }
        finally
        {
            gate.Dispose();
        }
    }
}
