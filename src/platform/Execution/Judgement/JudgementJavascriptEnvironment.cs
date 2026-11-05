using System.Runtime.Versioning;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal static class JudgementJavascriptEnvironment
{
    internal static void Install(Engine engine, JudgementWorkerInitialization initialization, JudgementWorkerInvocation invocation,
        JudgementProbeBridge bridge, CancellationToken cancellation, Func<JsonElement, JsValue> convert)
    {
        var serializer = new Jint.Native.Json.JsonSerializer(engine);
        JsValue Probe(string method, JsValue[] arguments)
        {
            cancellation.ThrowIfCancellationRequested();
            if (arguments.Length > 1) throw new IOException("judgement_probe_options_invalid");
            var value = arguments.Length == 0 ? engine.Intrinsics.Object.Construct([]) : arguments[0];
            var encoded = serializer.Serialize(value);
            if (!encoded.IsString() || encoded.AsString().Length > 4096) throw new IOException("judgement_probe_options_invalid");
            var bytes = JudgementWorkerProtocol.Utf8.GetBytes(encoded.AsString());
            using var document = Serialization.StrictJson.Parse(bytes);
            return convert(bridge.Call(method, document.RootElement));
        }
        var probes = engine.Intrinsics.Object.Construct([]);
        probes.CreateDataProperty("processes", new ClrFunction(engine, "processes", (_, arguments) => Probe("processes", arguments)));
        probes.CreateDataProperty("windows", new ClrFunction(engine, "windows", (_, arguments) => Probe("windows", arguments)));
        probes.CreateDataProperty("httpGet", new ClrFunction(engine, "httpGet", (_, arguments) => Probe("httpGet", arguments)));
        var nexus = engine.Intrinsics.Object.Construct([]);
        nexus.CreateDataProperty("files", JudgementJavascriptFiles.Create(engine, initialization, invocation, cancellation, convert));
        nexus.CreateDataProperty("probes", probes);
        engine.SetValue("nexus", nexus);
        engine.Execute("Object.freeze(nexus.files); Object.freeze(nexus.probes); Object.freeze(nexus);");
    }
}
