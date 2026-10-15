namespace NexusPipeline.Host.Updates;

internal sealed record UpdateWindowState(string Current, string? Latest, string State, bool CanDownload,
    int? Progress, string Message);

internal sealed class UpdateWindow : Form
{
    private readonly Func<CancellationToken, Task<UpdateWindowState>> _status;
    private readonly Func<string, CancellationToken, Task> _command;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private readonly Label _versions = new() { AutoSize = true };
    private readonly TextBox _message = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Button _check, _download, _apply, _cancel;
    private readonly bool _chinese;
    private bool _busy;
    private bool _polling;

    internal UpdateWindow(bool chinese, Func<CancellationToken, Task<UpdateWindowState>> status,
        Func<string, CancellationToken, Task> command)
    {
        _chinese = chinese; _status = status; _command = command;
        Text = "NexusPipeline Update";
        MinimumSize = new Size(540, 320); Size = new Size(640, 380);
        StartPosition = FormStartPosition.CenterScreen;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        Button Button(string zh, string en, string action)
        {
            var button = new Button { Text = chinese ? zh : en, AutoSize = true, MinimumSize = new Size(100, 40) };
            button.Click += async (_, _) => await Execute(action);
            buttons.Controls.Add(button); return button;
        }
        _check = Button("检查更新", "Check", "check");
        _download = Button("下载", "Download", "download");
        _apply = Button("应用并重启", "Apply & restart", "apply");
        _cancel = Button("取消下载", "Cancel download", "cancel");
        panel.Controls.Add(_versions); panel.Controls.Add(_progress); panel.Controls.Add(_message); panel.Controls.Add(buttons);
        Controls.Add(panel);
        _timer.Tick += async (_, _) => await RefreshStatus();
        Shown += async (_, _) => { await RefreshStatus(); _timer.Start(); };
        FormClosed += (_, _) => { _timer.Stop(); _lifetime.Cancel(); };
    }

    private async Task Execute(string command)
    {
        if (_busy) return;
        _busy = true; SetButtons(null);
        try
        {
            await _command(command, _lifetime.Token);
            if (!IsDisposed && command == "apply") Close();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!IsDisposed) _message.Text = error.Message; }
        finally { _busy = false; if (!IsDisposed) await RefreshStatus(); }
    }

    private async Task RefreshStatus()
    {
        if (_polling || IsDisposed || _lifetime.IsCancellationRequested) return;
        _polling = true;
        try
        {
            var state = await _status(_lifetime.Token);
            if (IsDisposed) return;
            _versions.Text = (_chinese ? "当前版本：" : "Current: ") + state.Current
                + (_chinese ? "    可用版本：" : "    Available: ") + (state.Latest ?? "—");
            _progress.Value = Math.Clamp(state.Progress ?? 0, 0, 100);
            if (state.State == "manualadoptionpending") _message.Text = _chinese
                ? "手动覆盖后的安装归属尚未核验。点击检查会下载官方更新包和完整包进行核验；离线时请联网重试或运行新版 Setup。"
                : "Installation ownership needs verification after manual copying. Check downloads the official Update and Portable packages as proof. Connect to the network or run the new Setup.";
            else if (!string.IsNullOrWhiteSpace(state.Message)) _message.Text = state.Message;
            SetButtons(state);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!IsDisposed) { _message.Text = error.Message; SetButtons(null); } }
        finally { _polling = false; }
    }

    private void SetButtons(UpdateWindowState? state)
    {
        _check.Enabled = !_busy && state?.State is "idle" or "manualadoptionpending";
        _download.Enabled = !_busy && state is { State: "idle", CanDownload: true };
        _apply.Enabled = !_busy && state?.State == "ready";
        _cancel.Enabled = !_busy && state?.State == "downloading";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}
