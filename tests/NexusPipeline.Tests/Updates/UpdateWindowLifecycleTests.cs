using System.Windows.Forms;
using NexusPipeline.Updater;
using Xunit;
namespace NexusPipeline.Tests.Updates;
public sealed class UpdateWindowLifecycleTests
{
    [Fact]
    public async Task OneDownloadCommandRemainsCancellableAndClosingDrainsOnlyTheClientSession()
    {
        var session = new Session();
        var shown = new TaskCompletionSource<UpdateWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                using var window = new UpdateWindow(session, false) { ShowInTaskbar = false, Opacity = 0 };
                window.Shown += (_, _) => {
                    var buttons = Descendants(window).OfType<Button>().ToArray();
                    Assert.Equal(2, buttons.Length);
                    Assert.Contains("[update_failed]", Descendants(window).OfType<TextBox>().Single().Text);
                    var primary = buttons.Single(button => button.Text == "Download");
                    primary.PerformClick(); primary.PerformClick();
                    shown.TrySetResult(window);
                };
                Application.Run(window);
                window.Dispose(); window.Dispose();
                finished.TrySetResult();
            }
            catch (Exception error) { shown.TrySetException(error); finished.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        UpdateWindow? ownedWindow = null;
        try
        {
            var window = ownedWindow = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "download" }, session.Commands);
            session.AcceptDownload.TrySetResult();
            await session.DownloadingRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            window.BeginInvoke(new Action(() => {
                var primary = Descendants(window).OfType<Button>().Single(button => button.Text == "Cancel download");
                Assert.True(primary.Enabled);
                Assert.Equal("", Descendants(window).OfType<TextBox>().Single().Text);
                window.Close();
            }));
            await session.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(finished.Task.IsCompleted);
            Assert.Equal(new[] { "download" }, session.Commands);
            session.ReleaseClose.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("downloading", session.State);
            Assert.Equal(1, session.CloseCount);
        }
        finally
        {
            session.AcceptDownload.TrySetResult(); session.ReleaseClose.TrySetResult();
            if (ownedWindow is not null && !finished.Task.IsCompleted)
                ownedWindow.BeginInvoke(new Action(ownedWindow.Close));
            thread.Join(TimeSpan.FromSeconds(2));
            session.Dispose();
        }
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private sealed class Session : IUpdateSession
    {
        internal List<string> Commands { get; } = [];
        internal TaskCompletionSource AcceptDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DownloadingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string State = "idle";
        internal int CloseCount;
        public Task<UpdateWindowState> ReadStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (State == "downloading") DownloadingRead.TrySetResult();
            return Task.FromResult(new UpdateWindowState("0.17.4", "0.17.5", State, true, 30, State == "idle" ? "update_failed" : ""));
        }
        public async Task CommandAsync(string action, CancellationToken token)
        {
            Commands.Add(action);
            if (action == "download") { await AcceptDownload.Task.WaitAsync(token); State = "downloading"; }
        }
        public async Task CloseAsync() { CloseCount++; CloseStarted.TrySetResult(); await ReleaseClose.Task; }
        public void Dispose() { }
    }
}
