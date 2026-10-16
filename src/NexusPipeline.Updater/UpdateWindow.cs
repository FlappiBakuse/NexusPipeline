namespace NexusPipeline.Updater;
internal enum PrimaryAction { None, Download, CancelDownload, ApplyRestart }
internal sealed record PrimaryButtonState(PrimaryAction Action, string Chinese, string English, bool Enabled);
internal static class UpdateButtons
{
    internal static PrimaryButtonState Map(UpdateWindowState? state, bool commandPending)
    {
        var value = state?.State switch
        {
            "idle" => new PrimaryButtonState(PrimaryAction.Download, "下载", "Download", state.CanDownload),
            "checking" => new(PrimaryAction.None, "正在检查…", "Checking…", false),
            "downloading" => new(PrimaryAction.CancelDownload, "取消下载", "Cancel download", true),
            "cancelling" => new(PrimaryAction.None, "正在取消…", "Cancelling…", false),
            "ready" => new(PrimaryAction.ApplyRestart, "应用并重启", "Apply & restart", true),
            "applypending" => new(PrimaryAction.None, "已安排应用", "Apply scheduled", false),
            "applying" => new(PrimaryAction.None, "正在应用…", "Applying…", false),
            "recoverypending" => new(PrimaryAction.None, "需要恢复", "Recovery required", false),
            "manualadoptionpending" => new(PrimaryAction.None, "下载", "Download", false),
            _ => new(PrimaryAction.None, "不可操作", "Unavailable", false),
        };
        return commandPending ? value with { Enabled = false } : value;
    }
}
internal sealed class UpdateWindow : Form
{
    private readonly IUpdateSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private readonly Label _versions = new() { AutoSize = true };
    private readonly TextBox _message = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Button _check = new() { AutoSize = true, MinimumSize = new Size(120,40) };
    private readonly Button _primary = new() { AutoSize = true, MinimumSize = new Size(140,40) };
    private readonly bool _chinese;
    private PrimaryAction _action;
    private UpdateWindowState? _snapshot;
    private bool _busy, _polling, _closing, _allowClose, _resourcesDisposed;
    private long _requestGeneration;
    private string? _commandError;
    private Task _activeCommand = Task.CompletedTask, _activePoll = Task.CompletedTask;
    private bool _commandErrorShown;
    internal UpdateWindow(IUpdateSession session, bool chinese)
    {
        _session = session; _chinese = chinese;
        Text = "NexusPipeline.update"; MinimumSize = new Size(540,320); Size = new Size(640,380); StartPosition = FormStartPosition.CenterScreen;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent,100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute,56));
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_check,0,0); buttons.Controls.Add(_primary,2,0);
        panel.Controls.Add(_versions); panel.Controls.Add(_progress); panel.Controls.Add(_message); panel.Controls.Add(buttons); Controls.Add(panel);
        _check.Click += async (_,_) => await Execute("check");
        _primary.Click += async (_,_) => { string? action = _action switch { PrimaryAction.Download => "download", PrimaryAction.CancelDownload => "cancel", PrimaryAction.ApplyRestart => "apply", _ => null }; if (action is not null) await Execute(action); };
        _timer.Tick += async (_,_) => await RefreshStatus();
        Shown += async (_,_) => { await RefreshStatus(); if (!_closing) _timer.Start(); };
        FormClosing += ClosingAsync;
        SetButtons(null);
    }
    private async Task Execute(string command)
    {
        if (_busy || _closing) return;
        _busy = true; _commandError = null; _requestGeneration++; SetButtons(_snapshot);
        try
        {
            _activeCommand = _session.CommandAsync(command, _lifetime.Token);
            await _activeCommand;
            if (command == "apply" && !_closing) { _message.Text = _chinese ? "已交给更新进程" : "Handed to the update process"; Close(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) { _commandError = error.Message; _commandErrorShown = false; } }
        finally { _busy = false; if (!_closing) { await RefreshStatus(); SetButtons(_snapshot); } }
    }
    private Task RefreshStatus()
    {
        if (_polling || _closing || IsDisposed) return Task.CompletedTask;
        _activePoll = ReadStatusCore();
        return _activePoll;
    }
    private async Task ReadStatusCore()
    {
        if (_polling || _closing || IsDisposed) return;
        long generation = _requestGeneration; _polling = true;
        try
        {
            var state = await _session.ReadStatusAsync(_lifetime.Token);
            if (_closing || IsDisposed || generation != _requestGeneration) return;
            _snapshot = state;
            if (_commandErrorShown) _commandError = null;
            if (_commandError is not null) _commandErrorShown = true;
            _versions.Text = (_chinese ? "当前版本：" : "Current: ") + state.Current + (_chinese ? "    可用版本：" : "    Available: ") + (state.Latest ?? "—");
            _progress.Value = Math.Clamp(state.Progress ?? 0,0,100);
            _message.Text = state.State == "manualadoptionpending" ? (_chinese ? "安装归属需要核验。核验会下载官方更新包和完整包；离线时请联网重试或运行新版 Setup。" : "Installation ownership needs verification. Verification downloads official Update and Portable packages; connect to the network or run Setup.") : Describe(_commandError ?? state.Message);
            if (state.State is not ("idle" or "manualadoptionpending" or "checking" or "downloading" or "cancelling" or "ready" or "applypending" or "applying" or "recoverypending")) _message.Text = "update.unknown_state: " + state.State;
            SetButtons(state);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing && generation == _requestGeneration) { _snapshot = null; _message.Text = Describe(error.Message); SetButtons(null); } }
        finally { _polling = false; }
    }
    private void SetButtons(UpdateWindowState? state)
    {
        var button = UpdateButtons.Map(state, _busy || _closing); _action = button.Action;
        _primary.Text = _chinese ? button.Chinese : button.English; _primary.AccessibleName = _primary.Text; _primary.Enabled = button.Enabled;
        _check.Text = state?.State == "manualadoptionpending" ? (_chinese ? "核验安装" : "Verify installation") : (_chinese ? "检查更新" : "Check");
        _check.Enabled = !_busy && !_closing && state?.State is "idle" or "manualadoptionpending";
    }
    private string Describe(string message)
    {
        string code = message.StartsWith("installer.tools_migration_requires_setup", StringComparison.Ordinal) ? "installer.tools_migration_requires_setup"
            : message.StartsWith("installer.uninstall_requires_setup", StringComparison.Ordinal) ? "installer.uninstall_requires_setup" : message;
        string? description = code switch
        {
        "update_failed" => _chinese ? "更新操作失败，请查看更新日志。" : "The update operation failed. Check the update log.",
        "update.cleanup_preserved" => _chinese ? "下载清理未完成，已保留现场。请先恢复或检查更新日志。" : "Download cleanup could not finish. The files were preserved; recover them or check the update log.",
        "update.service_timeout" => _chinese ? "更新服务响应超时，请重试。" : "The update service timed out. Please retry.",
        "update.service_unavailable" => _chinese ? "无法连接更新服务，请确认宿主仍在运行。" : "The update service is unavailable. Check that the Host is running.",
        "update.service_response_invalid" => _chinese ? "更新服务响应无效，请查看更新日志。" : "The update service returned an invalid response. Check the update log.",
        "installer.tools_migration_requires_setup" or "installer.uninstall_requires_setup" => _chinese ? "安装专用工具需要新版 Setup 修复；请保留当前数据并运行 Setup。" : "Run the new Setup to repair the installer tools, preserving your current data.",
        _ => null,
        };
        return description is null ? message : description + Environment.NewLine + "[" + code + "]";
    }
    private async void ClosingAsync(object? sender, FormClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true; _timer.Stop(); _requestGeneration++; _lifetime.Cancel(); SetButtons(_snapshot);
        _message.Text = _chinese ? "正在结束本窗口操作并清理…" : "Finishing window operations and cleanup…";
        try
        {
            await _session.CloseAsync();
            try { await _activePoll; } catch (OperationCanceledException) { } catch (Exception) { }
            try { await _activeCommand; } catch (OperationCanceledException) { } catch (Exception) { }
            if (_session is LocalUpdateSession)
            {
                var state = await _session.ReadStatusAsync(CancellationToken.None);
                if (state.State == "recoverypending") MessageBox.Show(this, Describe(state.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception error) { MessageBox.Show(this, Describe(error.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        _allowClose = true; Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed) { _resourcesDisposed = true; _timer.Stop(); _timer.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
