using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class UpdateDialog : IDisposable
{
    private const int CheckButton = 100, PrimaryButton = 101, CloseButton = 2;
    private readonly bool _chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly UpdateDialogNative.Notify _notify;
    private UpdateWindowStatus _status = new(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3), null, UpdateWindowPhase.Idle);
    private UpdateWindowStatus? _rendered;
    private UpdateWindowClient? _client;
    private Task _flight = Task.CompletedTask;
    private bool _opened, _closing, _allowClose;
    private string _primaryLabel;

    private UpdateDialog()
    {
        _notify = OnNotify;
        _primaryLabel = Text("应用并重启", "Apply and restart");
    }

    internal static int Show()
    {
        using var dialog = new UpdateDialog();
        try
        {
            UpdateDialogNative.Show(dialog.Page(dialog.Text("正在连接维护会话…", "Connecting to the update session…")), dialog._notify);
            return 0;
        }
        catch (Exception error) when (UpdateWindowPipe.IsFailure(error) || error is System.Runtime.InteropServices.ExternalException)
        {
            UpdateDialogNative.Error(dialog.Text("更新窗口未能打开。", "The update window could not open.") +
                Environment.NewLine + UpdateWindowPipe.Reason(error));
            return 1;
        }
    }

    private int OnNotify(nint window, uint notification, nint parameter, nint unused, nint data)
    {
        if (notification == UpdateDialogNative.Created && !_opened)
        {
            _opened = true;
            Start(UpdateWindowCommand.Read);
            Render(window);
        }
        if (notification == UpdateDialogNative.Timer && _flight.IsCompleted)
        {
            if (_status.Phase is UpdateWindowPhase.HandedOff or UpdateWindowPhase.Closed)
            {
                _allowClose = true;
                UpdateDialogNative.Close(window, CloseButton);
            }
            else if (_closing && _client is null)
            {
                _allowClose = true;
                UpdateDialogNative.Close(window, CloseButton);
            }
            else
            {
                Render(window);
                if (_closing) Start(UpdateWindowCommand.Close);
                else if (_client is not null && _status.Phase is UpdateWindowPhase.Checking or
                    UpdateWindowPhase.Downloading or UpdateWindowPhase.Cancelling) Start(UpdateWindowCommand.Read);
            }
        }
        if (notification != UpdateDialogNative.Clicked) return 0;
        if (parameter == CloseButton)
        {
            if (_allowClose) return 0;
            _closing = true;
            Render(window);
            return 1;
        }
        if (!_closing && _flight.IsCompleted)
        {
            var command = parameter == CheckButton ? UpdateWindowCommand.Check : Primary(_status.Phase);
            if (command is { } action)
            {
                Start(action);
                UpdateDialogNative.Enable(window, CheckButton, false);
                UpdateDialogNative.Enable(window, PrimaryButton, false);
            }
        }
        return 1;
    }

    private void Start(UpdateWindowCommand command) => _flight = Task.Run(async () =>
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            _client ??= await UpdateWindowClient.OpenAsync(deadline.Token);
            _status = await _client.ExecuteAsync(command, deadline.Token);
        }
        catch (Exception error) when (UpdateWindowPipe.IsFailure(error))
        {
            _client?.Dispose(); _client = null;
            _status = _status with { Phase = UpdateWindowPhase.Blocked, Reason = UpdateWindowPipe.Reason(error) };
        }
    });

    private void Render(nint window)
    {
        var ready = _flight.IsCompleted && !_closing;
        var state = _status;
        var primary = Primary(state.Phase);
        var label = primary switch
        {
            UpdateWindowCommand.Download => Text("下载", "Download"),
            UpdateWindowCommand.Cancel => Text("取消下载", "Cancel download"),
            _ => Text("应用并重启", "Apply and restart")
        };
        var percent = state.Total == 0 ? 0 : (int)Math.Clamp(state.Received * 100 / state.Total, 0, 100);
        var versions = Text("当前版本", "Current version") + $"  v{state.Current}    " +
            Text("可用版本", "Available version") + "  " + (state.Available is null ? "—" : "v" + state.Available);
        var content = versions + "\n\n" + Describe(state, percent);
        if (_primaryLabel != label)
        {
            _primaryLabel = label;
            UpdateDialogNative.Change(window, Page(content), _notify);
        }
        else if (_rendered != state || _closing) UpdateDialogNative.Content(window, content);
        UpdateDialogNative.Enable(window, CheckButton, ready && (state.Phase is UpdateWindowPhase.Idle or UpdateWindowPhase.Current or UpdateWindowPhase.Available ||
            state.Phase == UpdateWindowPhase.Blocked && _client is null));
        UpdateDialogNative.Enable(window, PrimaryButton, ready && primary is not null);
        if (_rendered != state) UpdateDialogNative.Progress(window, percent);
        _rendered = state;
    }

    private UpdateDialogNative.Page Page(string content) =>
        new(Text("NexusPipeline 更新", "NexusPipeline Update"), content,
            new(CheckButton, Text("检查更新", "Check for updates")), new(PrimaryButton, _primaryLabel), new(CloseButton, Text("关闭", "Close")));

    private string Describe(UpdateWindowStatus status, int percent)
    {
        if (_closing) return Text("正在结束窗口操作并清理…", "Finishing window operations and cleanup…");
        if (status.Reason is { } reason)
            return Text("更新操作未完成。请检查本机实例或更新来源后重试。", "Update operation incomplete. Check this installation or its update source and try again.") +
                $"\n{reason}";
        return status.Phase switch
        {
            UpdateWindowPhase.Checking => Text("正在检查更新…", "Checking for updates…"),
            UpdateWindowPhase.Current => Text("当前没有可用更新。", "No newer update is available."),
            UpdateWindowPhase.Available => Text("可以下载可用更新。", "An update is available to download."),
            UpdateWindowPhase.Downloading => Text("正在下载并验证…", "Downloading and verifying…") + $"  {percent}%",
            UpdateWindowPhase.Cancelling => Text("正在取消并清理…", "Cancelling and cleaning up…"),
            UpdateWindowPhase.Ready => Text("更新已验证。应用后将重启 NexusPipeline。", "Update verified. NexusPipeline will restart after applying it."),
            _ => Text("选择“检查更新”以查看可用版本。", "Select Check for updates to look for a newer version.")
        };
    }

    private static UpdateWindowCommand? Primary(UpdateWindowPhase phase) => phase switch
    {
        UpdateWindowPhase.Available => UpdateWindowCommand.Download,
        UpdateWindowPhase.Downloading => UpdateWindowCommand.Cancel,
        UpdateWindowPhase.Ready => UpdateWindowCommand.Apply,
        _ => null
    };

    private string Text(string chinese, string english) => _chinese ? chinese : english;

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _flight.GetAwaiter().GetResult(); }
        finally { _client?.Dispose(); _lifetime.Dispose(); }
    }
}
