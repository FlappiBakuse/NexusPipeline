using Jint;
using Jint.Native;
using Jint.Runtime.Interop;
using NexusPipeline.Core.Execution;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal static class BoundedJudgementScript
{
    internal static BusinessJudgementResult Execute(JudgementWorkerInitialization initialization,
        JudgementWorkerInvocation invocation, JudgementConsole console, JudgementDiagnostics diagnostics, JudgementProbeBridge probes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new Engine(options => options.Strict().LimitMemory(32 * 1024 * 1024)
            .MaxStatements(1_000_000).LimitRecursion(128)
            .TimeoutInterval(TimeSpan.FromSeconds(initialization.InvocationTimeoutSeconds)).CancellationToken(cancellationToken));
        engine.SetValue("__nexusInputValue", ConvertInput(engine, invocation.Input, cancellationToken));
        engine.Execute("const __nexusInput = __nexusInputValue; delete globalThis.__nexusInputValue;");
        console.Install(engine, cancellationToken);
        JudgementJavascriptEnvironment.Install(engine, initialization, invocation, diagnostics, probes, cancellationToken, input => ConvertInput(engine, input, cancellationToken));
        engine.Execute(initialization.Script);
        var result = engine.Evaluate("JSON.stringify(globalThis.nexusJudge(__nexusInput))").AsString();
        var bytes = JudgementWorkerProtocol.Utf8.GetBytes(result);
        cancellationToken.ThrowIfCancellationRequested();
        return JudgementWorkerProtocol.ReadResult(bytes);
    }

    private static JsValue ConvertInput(Engine engine, JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (input.ValueKind)
        {
            case JsonValueKind.Object:
                var result = engine.Intrinsics.Object.Construct([]);
                foreach (var property in input.EnumerateObject())
                    if (!result.CreateDataProperty(property.Name, ConvertInput(engine, property.Value, cancellationToken)))
                        throw new JsonException("judgement_input_property_rejected");
                return result;
            case JsonValueKind.Array:
                return new JsArray(engine, input.EnumerateArray().Select(value => ConvertInput(engine, value, cancellationToken)).ToArray());
            case JsonValueKind.String: return input.GetString()!;
            case JsonValueKind.Number: return input.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return JsValue.Null;
            default: throw new JsonException("judgement_input_unrecognized");
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class JudgementJavascriptEnvironment
{
    internal static void Install(Engine engine, JudgementWorkerInitialization initialization, JudgementWorkerInvocation invocation,
        JudgementDiagnostics diagnostics, JudgementProbeBridge bridge, CancellationToken cancellation, Func<JsonElement, JsValue> convert)
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
        nexus.CreateDataProperty("files", JudgementJavascriptFiles.Create(engine, initialization, invocation, diagnostics, cancellation, convert));
        nexus.CreateDataProperty("probes", probes);
        engine.SetValue("nexus", nexus);
        engine.Execute("Object.freeze(nexus.files); Object.freeze(nexus.probes); Object.freeze(nexus);");
    }
}

[SupportedOSPlatform("windows")]
internal static class JudgementJavascriptFiles
{
    internal static Jint.Native.Object.ObjectInstance Create(Engine engine, JudgementWorkerInitialization initialization, JudgementWorkerInvocation invocation,
        JudgementDiagnostics diagnostics, CancellationToken cancellation, Func<JsonElement, JsValue> convert)
    {
        var access = new JudgementFileAccess(initialization, invocation.Input, diagnostics, cancellation);
        var files = engine.Intrinsics.Object.Construct([]);
        files.CreateDataProperty("readText", new ClrFunction(engine, "readText", (_, arguments) =>
        {
            if (arguments.Length != 1 || !arguments[0].IsString()) return JsValue.Null;
            var text = access.ReadText(arguments[0].AsString()); return text is null ? JsValue.Null : (JsValue)text;
        }));
        files.CreateDataProperty("writeText", new ClrFunction(engine, "writeText", (_, arguments) =>
            arguments.Length == 2 && arguments[0].IsString() && arguments[1].IsString() &&
            access.WriteText(arguments[0].AsString(), arguments[1].AsString())));
        files.CreateDataProperty("list", new ClrFunction(engine, "list", (_, arguments) =>
            arguments.Length == 0 ? convert(access.Files) : JsValue.Null));
        return files;
    }
}
