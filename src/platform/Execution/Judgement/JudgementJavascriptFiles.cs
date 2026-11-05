using System.Runtime.Versioning;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal static class JudgementJavascriptFiles
{
    internal static Jint.Native.Object.ObjectInstance Create(Engine engine, JudgementWorkerInitialization initialization, JudgementWorkerInvocation invocation,
        CancellationToken cancellation, Func<JsonElement, JsValue> convert)
    {
        var access = new JudgementFileAccess(initialization, invocation.Input, cancellation);
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
