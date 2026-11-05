using System.Runtime.InteropServices;
using System.Text.Json;

internal static class MaaAgentFixture
{
    internal static int Run(string marker, string identifier)
    {
        OwnedFixtureEvidence.RequirePath(marker);
        var native = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "MaaFramework.dll"), typeof(MaaAgentFixture).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        var server = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "MaaAgentServer.dll"), typeof(MaaAgentFixture).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        try
        {
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(server, name));
            var option = Marshal.GetDelegateForFunctionPointer<OptionCall>(NativeLibrary.GetExport(native, "MaaGlobalSetOption"));
            if (option(1, [], 0) == 0 || option(4, BitConverter.GetBytes(0), 4) == 0) return 2;
            ActionCall action = (_, _, _, _, parameters, _, _, _) => {
                try
                {
                    using var parsed = JsonDocument.Parse(Marshal.PtrToStringUTF8(parameters)!);
                    if (parsed.RootElement.GetProperty("text").GetString() != "中文 Agent") return 0;
                    if (parsed.RootElement.TryGetProperty("retryTask", out var retryTask))
                    {
                        var task = retryTask.GetString();
                        if (task is not ("login" or "completed" or "failed" or "pending")) return 0;
                        File.AppendAllText(marker, task + "\n");
                        var failAt = parsed.RootElement.TryGetProperty("failAtCall", out var call) ? call.GetInt32() : 1;
                        if (task == "failed" && !File.Exists(marker + ".failed") && File.ReadAllLines(marker).Count(line => line == "failed") == failAt)
                        {
                            File.WriteAllText(marker + ".failed", "failed"); return 0;
                        }
                        return 1;
                    }
                    File.AppendAllText(marker, "action\n"); return 1;
                }
                catch { return 0; }
            };
            if (Export<RegisterCall>("MaaAgentServerRegisterCustomAction")("OwnedAgentAction", Marshal.GetFunctionPointerForDelegate(action), 0) == 0 ||
                Export<StartupCall>("MaaAgentServerStartUp")(identifier) == 0) return 3;
            Export<VoidCall>("MaaAgentServerJoin")();
            Export<VoidCall>("MaaAgentServerShutDown")();
            File.WriteAllText(marker + ".stopped", "stopped"); GC.KeepAlive(action); return 0;
        }
        finally { NativeLibrary.Free(server); NativeLibrary.Free(native); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte OptionCall(int option, byte[] value, ulong size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ActionCall(nint context, long task, nint node,
        nint action, nint parameters, long recognition, nint box, nint argument);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte RegisterCall([MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint callback, nint argument);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte StartupCall([MarshalAs(UnmanagedType.LPUTF8Str)] string identifier);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidCall();
}
