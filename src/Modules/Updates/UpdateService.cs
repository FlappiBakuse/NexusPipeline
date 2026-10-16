using NexusPipeline.Shared.Logging;
using NexusPipeline.Platform.Storage;
using System.Reflection;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Testing;
using NexusPipeline.Shared.Versioning;
namespace NexusPipeline.Modules.Updates;

internal sealed class UpdateService : UpdateEngine
{
    private readonly object _applyGate = new();
    private readonly string _taskFile;
    private readonly Func<(HostMaintenanceLease? Lease, string? Reason)> _acquireMaintenance;
    private readonly Func<bool> _requestExit, _isWebOnly;
    private readonly Func<string> _restartHandoff;
    private readonly Func<string, TimeSpan, bool> _stopDesktop;
    private readonly Func<DesktopResumeIntent> _captureDesktopIntent;
    private readonly Action<string, DesktopResumeIntent> _abortDesktop;
    private HostMaintenanceLease? _maintenanceLease;

    internal UpdateService(Func<AppSettings> settings, string installDir, Func<bool> canApply, Func<bool> requestExit,
        Func<(HostMaintenanceLease? Lease, string? Reason)>? acquireMaintenance = null,
        OutboundHttpClientProvider? outbound = null, Func<bool>? isWebOnly = null,
        Func<string>? restartHandoff = null, Func<string, TimeSpan, bool>? stopDesktop = null,
        Func<string, string?, bool, UpdatePayloadIdentity>? freezePayload = null,
        Func<DesktopResumeIntent>? captureDesktopIntent = null, Action<string, DesktopResumeIntent>? abortDesktop = null,
        bool setupTransition = false, string? sourceVersion = null)
        : base(() => Options(settings()), CurrentVersion, installDir, ClientFactory(outbound), freezePayload, setupTransition, sourceVersion)
    {
        _taskFile = Path.Combine(installDir, ".nxp-update", "task.json");
        _acquireMaintenance = acquireMaintenance ?? (() => canApply() ? (new HostMaintenanceLease(), null) : (null, "宿主当前繁忙"));
        _requestExit = requestExit; _isWebOnly = isWebOnly ?? (() => false); _restartHandoff = restartHandoff ?? (() => "");
        _stopDesktop = stopDesktop ?? ((_, _) => true); _captureDesktopIntent = captureDesktopIntent ?? (() => DesktopResumeIntent.FromVisible(false));
        _abortDesktop = abortDesktop ?? ((_, _) => {});
        NexusPipeline.Maintenance.MaintenanceLog.Sink = (level, message) => { if (level == "error") Logger.Error(message); else if (level == "warn") Logger.Warn(message); else Logger.Info(message); };
    }

    protected override IDisposable AcquireAdoptionAdmission()
    {
        var admission = _acquireMaintenance();
        return admission.Lease ?? throw new IOException(admission.Reason ?? "installer.adoption_busy");
    }

    public new UpdateApplyResult RequestApply(bool defer, string auditSource)
    {
        if (defer) return base.RequestApply(true, auditSource);
        lock (_applyGate)
        {
            var admission = _acquireMaintenance();
            if (admission.Lease is null) return UpdateApplyResult.Busy("busy", admission.Reason ?? "宿主当前繁忙");
            return ApplyWithLease(admission.Lease, () => base.RequestApply(false, auditSource));
        }
    }
    internal UpdateApplyResult RequestImmediateApplyWithLease(HostMaintenanceLease lease, string auditSource)
    { lock (_applyGate) return ApplyWithLease(lease, () => base.RequestApply(false, auditSource)); }
    internal new UpdateApplyResult RequestInstallerApply(string stagingDir, string version, string imageHash, string transactionId, string auditSource, string packageHash)
    {
        lock (_applyGate)
        {
            var admission = _acquireMaintenance();
            if (admission.Lease is null) return UpdateApplyResult.Busy("busy", admission.Reason ?? "宿主当前繁忙");
            return ApplyWithLease(admission.Lease, () => base.RequestInstallerApply(stagingDir, version, imageHash, transactionId, auditSource, packageHash));
        }
    }
    private UpdateApplyResult ApplyWithLease(HostMaintenanceLease lease, Func<UpdateApplyResult> apply)
    {
        _maintenanceLease = lease;
        var result = apply();
        if (!result.Succeeded) { _maintenanceLease = null; lease.Dispose(); }
        return result;
    }

    private static Func<Uri, TimeSpan, bool, HttpClient> ClientFactory(OutboundHttpClientProvider? outbound)
    {
        var provider = outbound ?? new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct);
        return (uri, timeout, redirects) => provider.CreateClient(uri, timeout, allowAutoRedirect: redirects);
    }
    internal static UpdateOptions Options(AppSettings settings) => new(EffectiveSourceUrl(settings), EffectiveChannel(settings));
    public static string EffectiveChannel(AppSettings settings) => settings.UpdateChannel is "stable" or "prerelease" ? settings.UpdateChannel : "prerelease";
    public static string EffectiveSourceUrl(AppSettings settings) => TestHooks.UpdateSourceUrl ?? settings.UpdateSourceUrl ?? "";
    public static string CurrentVersion
    {
        get
        {
            var assembly = typeof(UpdateService).Assembly;
            string? candidate = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0];
            return candidate is not null && NexusVersion.TryParse(candidate, out _) ? candidate : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
    protected override UpdateApplyResult Handoff(UpdateTask initial, string auditSource)
    {
        var lease = _maintenanceLease ?? throw new IOException("update.host_admission_missing");
        string transactionId = initial.TransactionId!, version = initial.Version, stagingDir = initial.StagedDir;
        UpdateWorkerLaunch workerLaunch = UpdateWorkerLaunch.NotStarted;
        bool desktopPrepared = false;

        UpdateTask? startedJournal = null;
        try
        {

            var journal = initial with { DesktopResumeIntent = _captureDesktopIntent() };
            journal.Write(_taskFile);
            startedJournal = journal;
            if (!_stopDesktop(transactionId, TimeSpan.FromSeconds(8))) throw new IOException("desktop_stop_unconfirmed");
            desktopPrepared = true;
            string handoffId = _restartHandoff();
            journal = journal with
            { DesktopStopped = true,
              RestartHandoffId = Guid.TryParseExact(handoffId, "N", out _) ? handoffId : null };
            journal.Write(_taskFile);
            startedJournal = journal;
            workerLaunch = UpdateApply.LaunchApplyWorker(stagingDir, _isWebOnly());
            if (workerLaunch == UpdateWorkerLaunch.NotStarted)
            {
                throw new InvalidOperationException("apply-update 子进程未能拉起");
            }
            Audit.Log(auditSource, "应用更新", $"v{version}（staging: {stagingDir}）");
            Logger.Info("[更新] apply-update 子进程已确认拉起，正在请求宿主退出。");
            bool exitRequested;
            try
            {
                exitRequested = _requestExit();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[更新] 宿主退出请求抛出异常，但 apply-update 已拉起；保留 journal 与维护租约：{ex.Message}");
                exitRequested = false;
            }
            if (!exitRequested)
            {
                // 维护租约仍然有效；worker 会等待宿主稍后释放互斥体，当前宿主不会再准入新操作。
                Logger.Warn("[更新] 宿主退出请求未立即完成，更新事务与维护租约保留，等待下一次安全退出。");
            }
            return UpdateApplyResult.Ok(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"[更新] 应用请求失败：{ex.Message}");
            if (workerLaunch != UpdateWorkerLaunch.NotStarted)
            {
                // worker 已经 armed，任何后续通知/退出异常都不能删除它的 journal。
                return UpdateApplyResult.Ok(false);
            }
            try { startedJournal?.Clear(_taskFile); }
            catch (Exception cleanupError) { Logger.Warn($"[更新] 保留不能确认归属的 journal：{cleanupError.Message}"); }
            if (desktopPrepared && startedJournal?.DesktopResumeIntent is { } intent)
            {
                try { _abortDesktop(transactionId!, intent); }
                catch (Exception abortError) { Logger.Warn("desktop_update_abort_failed: " + abortError.GetType().Name); }
            }
            return UpdateApplyResult.Busy("worker-launch-failed", $"无法启动更新切换：{ex.Message}");
        }
    }

}
