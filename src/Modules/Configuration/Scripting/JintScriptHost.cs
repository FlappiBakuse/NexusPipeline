using Jint;

namespace NexusPipeline.Modules.Configuration.Scripting;

/// <summary>Jint 脚本宿主的 API 面；调用方仍负责为每个脚本配置文件和写入策略。</summary>
internal enum JintScriptHostProfile
{
    Judge,
    ConfigValidation,
}

/// <summary>
/// 统一 Jint 引擎生命周期、输入绑定、日志收集和公共 glue。
/// Judge 与配置校验保留各自的 API 面，避免把某一边的文件策略带入另一边。
/// </summary>
internal sealed class JintScriptHost
{
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
        _engine.SetValue("__nexusLog", new Action<object?>(value => _outputs.Add(value?.ToString() ?? "")));
    }

    internal IReadOnlyList<string> Outputs => _outputs;

    internal static JintScriptHost Create(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int? maxStatements = null)
    {
        var host = new JintScriptHost(new Engine(options =>
        {
            options.TimeoutInterval(timeout);
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
        _engine.Execute(profile == JintScriptHostProfile.Judge ? JudgeGlue : ConfigValidationGlue);
        _engine.Execute(code);
    }
}
