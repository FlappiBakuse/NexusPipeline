using System.Text.Json;
using System.Runtime.Versioning;
using Jint;
using Jint.Native;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal static class BoundedJudgementScript
{
    internal static BusinessJudgementResult Execute(JudgementWorkerInitialization initialization,
        JudgementWorkerInvocation invocation, JudgementConsole console, JudgementProbeBridge probes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new Engine(options => options.Strict().LimitMemory(32 * 1024 * 1024)
            .MaxStatements(1_000_000).LimitRecursion(128)
            .TimeoutInterval(TimeSpan.FromSeconds(initialization.InvocationTimeoutSeconds)).CancellationToken(cancellationToken));
        engine.SetValue("__nexusInputValue", ConvertInput(engine, invocation.Input, cancellationToken));
        engine.Execute("const __nexusInput = __nexusInputValue; delete globalThis.__nexusInputValue;");
        console.Install(engine, cancellationToken);
        JudgementJavascriptEnvironment.Install(engine, initialization, invocation, probes, cancellationToken, input => ConvertInput(engine, input, cancellationToken));
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
