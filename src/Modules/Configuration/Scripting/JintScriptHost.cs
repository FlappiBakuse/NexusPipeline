using Jint;

namespace NexusPipeline.Modules.Configuration.Scripting;

/// <summary>Jint 脚本宿主的 API 面；调用方仍负责为每个脚本配置文件和写入策略。</summary>
internal enum JintScriptHostProfile
{
    Judge,
    ConfigValidation,
    TaskProtocol,
}

/// <summary>
/// 统一 Jint 引擎生命周期、输入绑定、日志收集和公共 glue。
/// Judge 与配置校验保留各自的 API 面，避免把某一边的文件策略带入另一边。
/// </summary>
internal sealed class JintScriptHost
{
    private const string TaskProtocolGlue = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const console = { log: (value) => __nexusLog(typeof value === "string" ? value : JSON.stringify(value)) };
        function __taskResource(value) {
          const result = JSON.parse(value);
          if (!result.ok) throw new Error('config_unavailable');
          return result.value;
        }
        const nexus = Object.freeze({
          input,
          readConfig: (id) => __taskResource(__nexusReadConfig(id)),
          readResource: (id) => __taskResource(__nexusReadResource(id)),
        });
        """;
    private const string JudgeGlue = """
        const console = { log: (...args) => args.forEach(a => __nexusLog(typeof a === "string" ? a : JSON.stringify(a))) };
        const nexus = {
          readFile: (p) => __nexusReadFile(p) || null,
          writeFile: (p, c) => __nexusWriteFile(p, c),
          listFiles: () => __nexusListFiles(),
          captureScreenshot: () => __nexusCaptureScreenshot(),
          listProcesses: (options) => JSON.parse(__nexusListProcesses(JSON.stringify(options || {}))),
          listWindows: (options) => JSON.parse(__nexusListWindows(JSON.stringify(options || {}))),
          httpGet: (url, options) => JSON.parse(__nexusHttpGet(url, JSON.stringify(options || {}))),
        };
        """;

    private const string ConfigValidationGlue = """
        const input = JSON.parse(__NEXUS_INPUT__);
        const nexus = {
          input,
          listFiles: () => __nexusListFiles(),
          readFile: (p) => __nexusReadFile(p),
          writeFile: (p, c) => __nexusWriteFile(p, c),
          exists: (p) => __nexusExists(p),
          toast: (m, k) => __nexusToast(m, k),
          notify: (t, b, k) => __nexusNotify(t, b, k),
        };
        """;

    private readonly Engine _engine;
    private readonly List<string> _outputs = new();

    private JintScriptHost(Engine engine)
    {
        _engine = engine;
        _engine.SetValue("__nexusLog", new Action<object?>(value =>
        {
            string text = value?.ToString() ?? "";
            if (_boundedOutput && (_outputs.Count > 0 || System.Text.Encoding.UTF8.GetByteCount(text) > 1024 * 1024))
                throw new InvalidDataException("resource_limit: task protocol must return one JSON result <= 1 MiB");
            _outputs.Add(text);
        }));
    }

    internal IReadOnlyList<string> Outputs => _outputs;
    private bool _boundedOutput;

    internal static JintScriptHost Create(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int? maxStatements = null,
        long? maxMemoryBytes = null)
    {
        var host = new JintScriptHost(new Engine(options =>
        {
            options.TimeoutInterval(timeout);
            if (maxMemoryBytes is long memory) options.LimitMemory(memory);
            if (maxStatements is int limit)
            {
                options.MaxStatements(limit);
            }
            options.CancellationToken(cancellationToken);
        }));
        return host;
    }

    internal void SetInput(string inputJson) => _engine.SetValue("__NEXUS_INPUT__", inputJson);

    internal void SetValue(string name, object value) => _engine.SetValue(name, value);

    internal void Execute(string code, JintScriptHostProfile profile)
    {
        _boundedOutput = profile == JintScriptHostProfile.TaskProtocol;
        _engine.Execute(profile switch
        {
            JintScriptHostProfile.Judge => JudgeGlue,
            JintScriptHostProfile.TaskProtocol => TaskProtocolGlue,
            _ => ConfigValidationGlue,
        });
        _engine.Execute(code);
    }
}
