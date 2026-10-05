using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Contracts;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Settings.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Tests.Support;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class ConfigRepairTests
{
    private static ConfigEditorDescriptor Editor(string? script = null) => new("fixture", "", "editor.js", script ?? """
        const {rule, document, context} = nexus.input;
        const previous = document[rule.selector[0]] ?? (rule.kind === 'mfa_tasks' ? nexus.readConfig('config:config.json')?.['Instance.default.TaskItems'] : undefined);
        let value = rule.toValue;
        if (rule.kind === 'bind_game_path') {
          if (!context.pc || !context.gamePath) value = previous;
          else value = context.gamePath;
        } else if (rule.kind === 'mfa_tasks') {
          value = previous.some(task => task.entry === 'CustomProgramAction') ? previous
            : [{entry:'CustomProgramAction',default_check:true}, ...previous];
        } else if (rule.fromValues.includes(previous)) value = previous;
        nexus.proposeRepair(value === previous ? null : {selector:rule.selector, value});
        """);
    private static TaskConfigRepairDescriptor Rule(string kind, string resource, string format, string field,
        string[]? safe = null, string value = "None") => new(kind, "fixture.rule", resource, new(field), "user_snapshot", format,
            safe ?? [], value, "仅修改当前快照") { Kind = kind };
    private static byte[]? Repair(TaskConfigRepairDescriptor rule, string text, ConfigRepairContext? context = null, ConfigEditorDescriptor? editor = null)
    {
        var proposal = ConfigRepairPolicy.Propose(rule, "fixture", "1", "user", "script", "profile", "locator", 1,
            Encoding.UTF8.GetBytes(text), context ?? new(@"C:\Games\Game.exe", "--owned", true), out var bytes, editor ?? Editor());
        Assert.Equal(proposal is null, bytes is null);
        return bytes;
    }

    [Fact]
    public void FiniteScalarRepairsPreserveSafeActionsCommentsAndBoundGamePath()
    {
        var rule = Rule("normalize_enum", "config:config.yaml", "yaml", "after_finish", ["None", "Exit"]);
        byte[] output = Repair(rule, "\uFEFF# keep\r\nafter_finish: RunScript # comment\r\nquota: 17\r\n")!;
        Assert.Equal("\uFEFF# keep\r\nafter_finish: \"None\" # comment\r\nquota: 17\r\n", Encoding.UTF8.GetString(output));
        Assert.Null(Repair(rule, "after_finish: Exit\n"));
        Assert.Null(Repair(rule, "after_finish: None\n"));
        foreach (var (field, none, allowed) in new[] { ("CompletionAction", "无", new[] { "", "无", "关闭游戏", "关闭软件", "关闭游戏和软件" }),
            ("AfterTask", "None", new[] { "None", "CloseMFA", "CloseEmulator", "CloseEmulatorAndMFA" }),
            ("POST_COMMAND", "", new[] { "" }) })
        {
            var action = Rule("normalize_enum", "config:$main", "json", field, allowed, none);
            foreach (string safe in allowed) Assert.Null(Repair(action, new JsonObject { [field] = safe }.ToJsonString()));
            var changed = JsonNode.Parse(Repair(action, new JsonObject { [field] = "UnknownSystemAction", ["quota"] = 17 }.ToJsonString())!)!;
            Assert.Equal(none, changed[field]!.GetValue<string>());
            Assert.Equal(17, changed["quota"]!.GetValue<int>());
        }
        var path = Rule("bind_game_path", "config:devices.json", "json", "pc_full_path");
        Assert.Equal(@"C:\Games\Game.exe", JsonNode.Parse(Repair(path, "{\"pc_full_path\":\"old\",\"capture\":\"WGC\"}")!)!["pc_full_path"]!.GetValue<string>());
        Assert.Null(Repair(path, "{}", new("", "", true)));
        Assert.Null(Repair(path, "{}", new(@"C:\Games\Game.exe", "", false)));
        Assert.NotNull(Repair(path, "{\"capture\":\"WGC\"}"));
    }

    [Fact]
    public void MaaRepairsInsertFirstLaunchDisablePowerAndKeepOtherInstancesAndArguments()
    {
        var mxu = Rule("mxu_tasks", "config:mxu.json", "json", "instances");
        const string input = "{\"settings\":{\"autoStartInstanceId\":\"a\"},\"instances\":[{\"id\":\"a\",\"tasks\":[{\"id\":\"reward\",\"taskName\":\"Daily\",\"enabled\":true},{\"id\":\"power\",\"taskName\":\"__MXU_POWER__\",\"enabled\":true,\"enabledByController\":{\"pc\":true}}]},{\"id\":\"b\",\"tasks\":[{\"id\":\"other\",\"enabled\":true}]}]}";
        var original = JsonNode.Parse(input)!;
        var intended = (JsonArray)original["instances"]![0]!["tasks"]!.DeepClone();
        intended[1]!["enabled"] = false;
        intended.Insert(0, new JsonObject { ["id"] = "launch", ["taskName"] = "__MXU_LAUNCH__", ["enabled"] = true });
        var selector = new JsonArray("instances", new JsonObject { ["by"] = "id", ["value"] = "a" }, "tasks");
        var suggestion = new JsonObject { ["selector"] = selector, ["value"] = intended };
        var editor = Editor("nexus.proposeRepair(" + suggestion.ToJsonString() + ");");
        byte[] changed = Repair(mxu, input, editor: editor)!;
        var json = JsonNode.Parse(changed)!;
        Assert.True(JsonNode.DeepEquals(original["instances"]![1], json["instances"]![1]));
        Assert.True(JsonNode.DeepEquals(intended, json["instances"]![0]!["tasks"]));
        Assert.Null(Repair(mxu, Encoding.UTF8.GetString(changed), editor: editor));
        suggestion["selector"]![1]!["value"] = "b";
        Assert.Throws<InvalidDataException>(() => Repair(mxu, input, editor: Editor("nexus.proposeRepair(" + suggestion.ToJsonString() + ");")));
        var preactions = Rule("mxu_preactions", "config:mxu.json", "json", "instances");
        suggestion["selector"]![1]!["value"] = "a";
        suggestion["selector"]![2] = "preActions";
        suggestion["value"] = new JsonArray(new JsonObject { ["id"] = "launch", ["program"] = @"C:\Games\Game.exe", ["args"] = "--keep", ["enabled"] = true, ["waitForExit"] = false });
        byte[] preactionChanged = Repair(preactions, input, editor: Editor("nexus.proposeRepair(" + suggestion.ToJsonString() + ");"))!;
        var preactionJson = JsonNode.Parse(preactionChanged)!;
        Assert.True(JsonNode.DeepEquals(original["instances"]![1], preactionJson["instances"]![1]));
        Assert.True(JsonNode.DeepEquals(original["instances"]![0]!["tasks"], preactionJson["instances"]![0]!["tasks"]));
        Assert.Equal("--keep", preactionJson["instances"]![0]!["preActions"]![0]!["args"]!.GetValue<string>());
        Assert.Null(Repair(preactions, Encoding.UTF8.GetString(preactionChanged), editor: Editor("nexus.proposeRepair(" + suggestion.ToJsonString() + ");")));
        suggestion["selector"]![1]!["value"] = "b";
        Assert.Throws<InvalidDataException>(() => Repair(preactions, input, editor: Editor("nexus.proposeRepair(" + suggestion.ToJsonString() + ");")));
        suggestion["selector"]![1]!["value"] = "a";
        suggestion["selector"]![2] = "tasks";
        Assert.Throws<InvalidDataException>(() => Repair(preactions, input, editor: Editor("nexus.proposeRepair(" + suggestion.ToJsonString() + ");")));
        var mfa = Rule("mfa_tasks", "config:instances/a.json", "json", "TaskItems");
        Assert.Throws<InvalidDataException>(() => Repair(mfa, "{}", editor: Editor("nexus.proposeRepair({selector:['other'],value:[]});")));
        Assert.Throws<InvalidDataException>(() => Repair(mfa, "{}", editor: Editor("nexus.proposeRepair(null); nexus.proposeRepair(null);")));
        Assert.ThrowsAny<Exception>(() => Repair(mfa, "{}", editor: Editor("nexus.writeFile('outside','bad');")));
        var doc = new TaskConfigDocument(Encoding.UTF8.GetBytes("{\"tasks\":[]}"), "json");
        Assert.Throws<InvalidDataException>(() => doc.Patch([new(new("tasks"), new JsonArray(), new JsonArray(new JsonObject()), "selection")], new HashSet<string> { "[\"tasks\"]" }));
    }

    [Fact]
    public async Task CommandsRepairMainAndExtraSnapshotsWithBackupCasAndAccountIsolation()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var declaration = new ScriptInstance { Id = Guid.NewGuid().ToString("N"), PluginType = "fixture", RootPath = root,
            GameExe = Path.Combine(root, "game.exe"), GameMode = "pc" };
        var plugins = new Capabilities(root);
        var resolver = new ScriptSpecResolver(plugins, plugins);
        var a = new NexusUser { Name = "A", Bindings = [new() { ScriptInstanceId = declaration.Id }] };
        var b = new NexusUser { Name = "B", Bindings = [new() { ScriptInstanceId = declaration.Id }] };
        var settings = new Settings();
        var assessment = new Assessment();
        var commands = new ConfigEditCommands(new Admission(), new Scripts(declaration), new Users(a, b), plugins,
            plugins, resolver, null!, taskProtocolAssessment: assessment, settingsProvider: settings);
        foreach (var user in new[] { a, b })
        {
            string store = ConfigPaths.StoreDir(declaration.Id, user.Id); Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "config.yaml"), "after_finish: Shutdown\nquota: 7\n");
            File.WriteAllText(Path.Combine(store, "other.txt"), "untouched");
            var meta = ConfigStoreMetadata.For(store, new ConfigSessionRuntimeMetadata("", "", "", "profile", "fixture", "1", "dir"));
            meta.ConfigLocatorHash = ConfigStoreMetadata.HashLocator(plugins.Profile.ConfigPath);
            ConfigStoreMetadata.Save(declaration.Id, user.Id, meta);
            string extra = ConfigPaths.StoreExtraDir(declaration.Id, user.Id, plugins.Profile.ExtraConfigPaths[0]); Directory.CreateDirectory(extra);
            File.WriteAllText(Path.Combine(extra, "one_dragon.yml"), "after_done: 关机\nkeep: yes\n");
        }
        string beforeAssessment = ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.StoreDir(declaration.Id, a.Id));
        var resolvedUser = new ResolvedScriptUser(a.Id, a.Name, a.Bindings[0]);
        var result = commands.RunConfigAssessment(declaration, resolvedUser, resolver.Resolve(declaration));
        Assert.True(result.Ran);
        Assert.Equal(a.Id, Assert.Single(result.Diagnostics).UserId);
        Assert.Equal(new[] { (a.Id, "config-edit") }, assessment.Calls);
        var saved = await new ScriptSaveValidation(resolver, new Users(a, b), assessment)
            .RunForScriptAsync(declaration);
        Assert.NotNull(saved);
        Assert.Equal(new[] { a.Id, b.Id }, saved.Diagnostics.Select(diagnostic => diagnostic.UserId));
        Assert.Equal(new[] { (a.Id, "config-edit"), (a.Id, "script-save"), (b.Id, "script-save") }, assessment.Calls);
        Assert.Equal(beforeAssessment, ConfigSnapshotFingerprint.Fingerprint(ConfigPaths.StoreDir(declaration.Id, a.Id)));
        Assert.False(commands.PreviewRepair(declaration.Id, a.Id).Value!.Available);
        settings.Current.AllowConfigRepair = true;
        var preview = commands.PreviewRepair(declaration.Id, a.Id);
        Assert.True(preview.Succeeded && preview.Value!.Available, preview.Error?.Message);
        var originalEditor = plugins.Profile.ConfigEditor!;
        plugins.Profile.ConfigEditor = originalEditor with { Script = originalEditor.Script + "\n;" };
        Assert.False(commands.ApplyRepair(declaration.Id, a.Id, preview.Value!.Token!).Succeeded);
        plugins.Profile.ConfigEditor = Editor("nexus.readConfig('config:../outside.json'); nexus.proposeRepair(null);");
        Assert.False(commands.PreviewRepair(declaration.Id, a.Id).Succeeded);
        plugins.Profile.ConfigEditor = originalEditor;
        Assert.False(commands.ApplyRepair(declaration.Id, b.Id, preview.Value!.Token!).Succeeded);
        declaration.GameExe = Path.Combine(root, "changed.exe");
        Assert.False(commands.ApplyRepair(declaration.Id, a.Id, preview.Value.Token!).Succeeded);
        preview = commands.PreviewRepair(declaration.Id, a.Id);
        var applied = commands.ApplyRepair(declaration.Id, a.Id, preview.Value!.Token!);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Contains("None", File.ReadAllText(Path.Combine(ConfigPaths.StoreDir(declaration.Id, a.Id), "config.yaml")));
        Assert.Contains("Shutdown", File.ReadAllText(Path.Combine(ConfigPaths.StoreDir(declaration.Id, b.Id), "config.yaml")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(ConfigPaths.StoreDir(declaration.Id, a.Id), "other.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(ConfigPaths.UserDir(declaration.Id, a.Id), "repair-backups")));
        preview = commands.PreviewRepair(declaration.Id, a.Id);
        Assert.True(preview.Value!.Available);
        applied = commands.ApplyRepair(declaration.Id, a.Id, preview.Value.Token!);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        string savedExtra = Path.Combine(ConfigPaths.StoreExtraDir(declaration.Id, a.Id, plugins.Profile.ExtraConfigPaths[0]), "one_dragon.yml");
        Assert.Equal("无", new TaskConfigDocument(File.ReadAllBytes(savedExtra), "yaml").ReadSelection(new("after_done"))!.GetValue<string>());
        Assert.False(commands.PreviewRepair(declaration.Id, a.Id).Value!.Available);
        Assert.False(File.Exists(plugins.Profile.ExtraConfigPaths[0]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ConfigPaths.WorkDir(declaration.Id, a.Id)));
        plugins.Profile.ConfigInputValue = "chosen";
        plugins.Profile.TaskProtocol = plugins.Profile.TaskProtocol! with { RepairRules = [Rule("mfa_tasks", "config:instances/{instance}.json", "json", "TaskItems")] };
        string main = ConfigPaths.StoreDir(declaration.Id, a.Id);
        Directory.CreateDirectory(Path.Combine(main, "instances"));
        File.WriteAllText(Path.Combine(main, "instances", "chosen.json"), "{\"other\":42}");
        string inherited = "{\"Instance.default.TaskItems\":[{\"entry\":\"Daily\",\"name\":\"日常\",\"default_check\":true}]}";
        File.WriteAllText(Path.Combine(main, "config.json"), inherited);
        preview = commands.PreviewRepair(declaration.Id, a.Id);
        Assert.True(preview.Value!.Available);
        applied = commands.ApplyRepair(declaration.Id, a.Id, preview.Value.Token!);
        Assert.True(applied.Succeeded, applied.Error?.Message);
        Assert.Equal(inherited, File.ReadAllText(Path.Combine(main, "config.json")));
        var local = JsonNode.Parse(File.ReadAllText(Path.Combine(main, "instances", "chosen.json")))!;
        Assert.Equal("CustomProgramAction", local["TaskItems"]![0]!["entry"]!.GetValue<string>());
        Assert.Equal(42, local["other"]!.GetValue<int>());
        Assert.False(commands.PreviewRepair(declaration.Id, a.Id).Value!.Available);
        string stage = Path.Combine(root, "cas-stage"); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "replacement.json"), "{}");
        string beforeMain = ConfigSnapshotFingerprint.Fingerprint(main);
        int checks = 0;
        Assert.Throws<IOException>(() => ConfigStoreTransaction.Apply(declaration.Id, a.Id, stage,
            new HashSet<string>(), null, null, new ConfigSessionMark
            {
                ScriptId = declaration.Id, UserId = a.Id, ConfigPath = plugins.Profile.ConfigPath,
                ConfigKind = "dir", PluginName = "fixture", PluginVersion = "1",
            }, preserveExistingMetadata: true, validateCurrent: () =>
            {
                if (++checks == 2) throw new IOException("fixture: context changed during staging");
            }));
        Assert.Equal(2, checks);
        Assert.Equal(beforeMain, ConfigSnapshotFingerprint.Fingerprint(main));
        Assert.False(Directory.Exists(ConfigPaths.StoreTransactionDir(declaration.Id, a.Id)));
        string extraRoot = Path.GetDirectoryName(savedExtra)!;
        string beforeExtra = ConfigSnapshotFingerprint.Fingerprint(extraRoot);
        checks = 0;
        var diff = ConfigStoreDiff.Build(stage, extraRoot, new HashSet<string>(), null);
        Assert.Throws<IOException>(() => ExtraConfigStoreTransaction.Apply(declaration.Id, a.Id,
            plugins.Profile.ExtraConfigPaths[0], extraRoot, diff, ConfigSwapSession.SampleConfig(stage),
            sourcePath: stage, validateCurrent: () =>
            {
                if (++checks == 2) throw new IOException("fixture: account snapshot changed during staging");
            }));
        Assert.Equal(2, checks);
        Assert.Equal(beforeExtra, ConfigSnapshotFingerprint.Fingerprint(extraRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ConfigPaths.WorkDir(declaration.Id, a.Id)));
    }

    private sealed class Scripts(ScriptInstance script) : IScriptRepository
    { public ScriptInstance? FindById(string id) => script.Id == id ? script : null; public IReadOnlyList<ScriptInstance> Snapshot() => [script]; }
    private sealed class Users : CurrentModelUserRepository, IUserSnapshotReader
    {
        private readonly NexusUser[] _users;
        internal Users(params NexusUser[] users) : base(users) => _users = users;
        public NexusUser? FindById(string id) => _users.FirstOrDefault(user => user.Id == id);
        public IReadOnlyList<NexusUser> Snapshot() => _users;
    }
    private sealed class Settings : ISettingsProvider { public AppSettings Current { get; } = new(); }
    private sealed class Assessment : ITaskProtocolConfigAssessmentPort
    {
        internal List<(string UserId, string Trigger)> Calls { get; } = [];
        public Task<TaskPlan?> RunAsync(ResolvedScriptSpec spec, ResolvedScriptUser user, string trigger,
            CancellationToken token = default)
        {
            Assert.Equal("0.2.0", spec.TaskProtocol!.Version);
            Calls.Add((user.UserId, trigger));
            return Task.FromResult<TaskPlan?>(new("0.2.0", "assessment", "fixture", "fixture", "1",
                DateTimeOffset.UtcNow, "signature", "full", [], []));
        }
        public IReadOnlyList<ConfigValidationDiagnostic> ToDiagnostics(TaskPlan plan, ResolvedScriptUser user) =>
            [new(user.Binding.ScriptInstanceId, user.UserId, user.UserName, "fixture.rule", "unsatisfied", "warning", "none", null, [], [])];
        public ConfigValidationDiagnostic Error(ResolvedScriptSpec spec, ResolvedScriptUser user, string message) =>
            throw new InvalidOperationException(message);
    }
    private sealed class Admission : IConfigEditAdmission
    {
        public bool TryExecuteLeaseMutation(string id, string? user, Action mutation, out IReadOnlyList<ExecutionLeaseReference> leases, out string? code)
        { leases = []; code = null; mutation(); return true; }
        public bool TryBeginEditSession(string id, string user, string path, out string? conflict) { conflict = null; return true; }
        public void EndEditSession(string id, string user) { }
    }
    private sealed class Capabilities : IPluginCapabilityResolver, IPluginAvailability
    {
        internal ScriptProfile Profile;
        internal Capabilities(string root) => Profile = new() { PluginName = "fixture", PluginVersion = "1", MainExe = Path.Combine(root, "fixture.exe"),
            ConfigEditor = Editor(), ConfigPath = Path.Combine(root, "configs"), ExtraConfigPaths = [Path.Combine(root, "one_dragon.yml")],
            TaskProtocol = new("0.2.0", "", "", "", []) { RepairRules = [Rule("normalize_enum", "config:config.yaml", "yaml", "after_finish", ["None", "Exit"]),
                Rule("normalize_enum", "extra:0", "yaml", "after_done", ["无", "关闭游戏"], "无")] } };
        public bool SupportsEmulator(string name) => false;
        public bool HasCapability(string name, string capability) => false;
        public ScriptProfile? ResolveProfile(string name, string root, IReadOnlyDictionary<string, string>? inputs = null) => Profile;
        public IReadOnlyList<string> GetMissingConfigCandidates(string name, string root, IReadOnlyDictionary<string, string>? inputs) => [];
        public bool IsKnownPlugin(string name) => true;
        public bool IsDataSpecializedPlugin(string name) => true;
        public bool IsEnabled(string name) => true;
    }
}
