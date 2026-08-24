using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins.Managed;

/// <summary>插件后台任务运行器：任务在独立异步循环中执行，单任务超时/异常不会穿透宿主。</summary>
internal sealed class PluginJobScheduler : IPluginJobScheduler, IDisposable
{
    private readonly string _pluginName;
    private readonly Action<Exception> _reportError;
    private readonly object _sync = new();
    private readonly Dictionary<string, JobRegistration> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PluginJobScheduler(string pluginName, Action<Exception> reportError)
    {
        _pluginName = pluginName;
        _reportError = reportError;
    }

    public IDisposable Register(PluginJobDefinition definition, Func<PluginJobContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        Validate(definition);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.ContainsKey(definition.Id))
            {
                throw new InvalidOperationException($"插件任务 ID 重复：{definition.Id}");
            }
            var job = new JobRegistration(_pluginName, definition, handler, _reportError);
            _jobs.Add(definition.Id, job);
            job.Start();
            return job;
        }
    }

    public void Dispose()
    {
        JobRegistration[] jobs;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            jobs = _jobs.Values.ToArray();
            _jobs.Clear();
        }
        foreach (JobRegistration job in jobs)
        {
            job.Dispose();
        }
    }

    private static void Validate(PluginJobDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new ArgumentException("插件任务 ID 不能为空。", nameof(definition));
        }
        if (definition.Interval is not null && definition.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentException("插件任务 interval 必须大于 0。", nameof(definition));
        }
        if (definition.DailyTime is null && definition.Interval is null)
        {
            throw new ArgumentException("插件任务必须提供 interval 或 dailyTime。", nameof(definition));
        }
        if (definition.Timeout is not null && definition.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("插件任务 timeout 必须大于 0。", nameof(definition));
        }
    }

    private sealed class JobRegistration : IDisposable
    {
        private readonly string _pluginName;
        private readonly PluginJobDefinition _definition;
        private readonly Func<PluginJobContext, CancellationToken, ValueTask> _handler;
        private readonly Action<Exception> _reportError;
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        private int _disposed;

        public JobRegistration(
            string pluginName,
            PluginJobDefinition definition,
            Func<PluginJobContext, CancellationToken, ValueTask> handler,
            Action<Exception> reportError)
        {
            _pluginName = pluginName;
            _definition = definition;
            _handler = handler;
            _reportError = reportError;
        }

        public void Start()
        {
            _loop = Task.Run(RunAsync);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _stop.Cancel();
                try
                {
                    _loop?.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // RunAsync 已记录任务异常；关停阶段继续释放 token 和插件加载上下文。
                }
                _stop.Dispose();
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    DateTimeOffset scheduledAt = NextOccurrence(DateTimeOffset.Now);
                    TimeSpan delay = scheduledAt - DateTimeOffset.Now;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                    }
                    if (_stop.IsCancellationRequested)
                    {
                        break;
                    }
                    DateTimeOffset startedAt = DateTimeOffset.Now;
                    var context = new PluginJobContext(_pluginName, _definition.Id, scheduledAt, startedAt);
                    try
                    {
                        Task work = _handler(context, _stop.Token).AsTask();
                        TimeSpan timeout = _definition.Timeout ?? TimeSpan.FromMinutes(5);
                        await work.WaitAsync(timeout, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _reportError(ex);
                    }
                    if (_definition.Interval is null && _definition.DailyTime is not null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        private DateTimeOffset NextOccurrence(DateTimeOffset now)
        {
            DateTimeOffset? interval = _definition.Interval is TimeSpan every ? now + every : null;
            DateTimeOffset? daily = null;
            if (_definition.DailyTime is TimeOnly time)
            {
                DateTime localDate = now.LocalDateTime.Date;
                DateTime candidate = localDate + time.ToTimeSpan();
                if (candidate <= now.LocalDateTime)
                {
                    candidate = candidate.AddDays(1);
                }
                daily = new DateTimeOffset(candidate);
            }
            return interval is null ? daily!.Value : daily is null ? interval.Value : (interval < daily ? interval.Value : daily.Value);
        }
    }
}
