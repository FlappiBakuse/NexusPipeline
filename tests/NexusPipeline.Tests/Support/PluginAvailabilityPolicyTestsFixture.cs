using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History.Contracts;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications.Contracts;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Tests.Execution;
using NexusPipeline.Tests.Scheduling;
namespace NexusPipeline.Tests.Support;


public sealed class PluginAvailabilityPolicyTestsFixture
{
internal static ScriptInstance SpecializedScript(string id, string name) => new()
    {
        Id = id,
        Name = name,
        PluginType = "missing-plugin-" + id,
        ConfigPath = "",
    };
internal sealed class FakePluginAvailability : IPluginAvailability
    {
        private readonly HashSet<string> _known;
        private readonly HashSet<string> _dataSpecialized;
        private readonly HashSet<string> _enabled;

        public FakePluginAvailability(
            IEnumerable<string>? known = null,
            IEnumerable<string>? dataSpecialized = null,
            IEnumerable<string>? enabled = null)
        {
            _known = new HashSet<string>(known ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _dataSpecialized = new HashSet<string>(dataSpecialized ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _enabled = new HashSet<string>(enabled ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public bool IsKnownPlugin(string pluginName) => _known.Contains(pluginName);

        public bool IsDataSpecializedPlugin(string pluginName) => _dataSpecialized.Contains(pluginName);

        public bool IsEnabled(string pluginName) => _enabled.Contains(pluginName);
    }
internal sealed class SingleScriptRepository : NexusPipeline.Modules.Scripts.Contracts.IScriptRepository
    {
        private readonly ScriptInstance _script;

        public SingleScriptRepository(ScriptInstance script)
        {
            _script = script;
        }

        public ScriptInstance? FindById(string id) => string.Equals(id, _script.Id, StringComparison.Ordinal) ? _script : null;

        public IReadOnlyList<ScriptInstance> Snapshot() => new[] { _script };
    }
internal sealed class EmptyQueueRepository : NexusPipeline.Modules.Queues.Contracts.IQueueRepository
    {
        public DispatchQueue? FindById(string id) => null;

        public IReadOnlyList<DispatchQueue> Snapshot() => Array.Empty<DispatchQueue>();
    }
internal sealed class EmptyUserRepository : CurrentModelUserRepository
    {
    }
internal static ExecutionRunner CreateRunner(
        IHistoryStore history,
        IPluginAvailability plugins)
    {
        return new ExecutionRunner(
            new EmptyUserRepository(),
            history,
            new NoopNotificationService(),
            new SystemActionExecutor(new ExecutionStateStore()),
            plugins,
            new EmptyEmulatorSupportProviderResolver());
    }
internal sealed class EmptyEmulatorSupportProviderResolver : IEmulatorSupportProviderResolver
    {
        public IReadOnlyList<EmulatorSupportProviderDescriptor> GetEmulatorSupportProviders() =>
            Array.Empty<EmulatorSupportProviderDescriptor>();
    }
internal sealed class CapturingHistoryStore : IHistoryStore
    {
        public List<RunRecord> Records { get; } = new();

        public HistorySaveResult Save(RunRecord record, List<string> attemptLogs, IReadOnlyList<RunScreenshot> screenshots)
        {
            Records.Add(record.Clone());
            return new HistorySaveResult(record.Clone(), null);
        }

        public IReadOnlyDictionary<string, int> GetSuccessfulRunsByUser(DateTime date, string scriptInstanceId) =>
            Records
                .Where(record => record.StartTime.Date == date.Date
                    && record.ScriptInstanceId == scriptInstanceId
                    && record.UserId.Length > 0
                    && record.Status == "success")
                .GroupBy(record => record.UserId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        public void Cleanup(int retentionDays)
        {
        }
    }
internal sealed class NoopNotificationService : INotificationService
    {
        public Task NotifyScriptAsync(ScriptInstance script, RunRecord record) => Task.CompletedTask;

        public Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records) => Task.CompletedTask;
    }
internal sealed class RuntimeDataScope : IDisposable
    {
        private readonly HostCompositionRoot _context;
        private readonly List<ScriptInstance> _previousScripts;
        private readonly List<DispatchQueue> _previousQueues;
        private readonly List<NexusUser> _previousUsers;
        private readonly bool _usersFileExists;
        private readonly byte[]? _usersFile;
        private readonly string _scriptDataDir;

        public RuntimeDataScope(HostCompositionRoot context, ScriptInstance script, params NexusUser[] users)
        {
            _context = context;
            _previousScripts = _context.EntityState.SnapshotScripts();
            _previousQueues = _context.EntityState.SnapshotQueues();
            _previousUsers = _context.EntityState.SnapshotUsers();
            _usersFileExists = File.Exists(AppPaths.UsersPath);
            _usersFile = _usersFileExists ? File.ReadAllBytes(AppPaths.UsersPath) : null;
            _scriptDataDir = Path.Combine(AppPaths.DataDir, script.Id);

            _context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.Add(script);
                state.Queues.Clear();
                state.Users.Clear();
                state.Users.AddRange(users);
            });
        }

        public void Dispose()
        {
            _context.EntityState.Mutate(state =>
            {
                state.Scripts.Clear();
                state.Scripts.AddRange(_previousScripts);
                state.Queues.Clear();
                state.Queues.AddRange(_previousQueues);
                state.Users.Clear();
                state.Users.AddRange(_previousUsers);
            });
            RestoreFile(AppPaths.UsersPath, _usersFileExists, _usersFile);
            DeleteExactDirectory(_scriptDataDir);
        }

        private static void RestoreFile(string path, bool existed, byte[]? bytes)
        {
            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes!);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void DeleteExactDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
