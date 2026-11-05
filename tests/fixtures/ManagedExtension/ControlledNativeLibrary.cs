using System.Runtime.InteropServices;
using System.Text.Json;
using NexusPipeline.Extensibility.Assets;
using NexusPipeline.Extensibility.Lifecycle;

namespace Controlled;

internal static class ControlledNativeLibrary
{
    internal static async ValueTask ActivateAsync(string mode, string evidenceDirectory,
        IExtensionPackageReadLease assets, IExtensionScope scope)
    {
        var path = assets.File("native/nexus-native-fixture.dll").Path;
        var requested = mode switch
        {
            "native-basename" => Path.GetFileName(path),
            "native-outside" => Path.Combine(Environment.SystemDirectory, "version.dll"),
            _ => path
        };
        var module = NativeLibrary.Load(requested, typeof(ControlledNativeLibrary).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        var owned = new Module(module, evidenceDirectory, Guid.NewGuid().ToString("N"));
        await scope.OwnAsync(owned);
        if (NativeLibrary.GetExport(module, "GetFileVersionInfoSizeW") == 0) throw new IOException("fixture_native_export_missing");
        File.WriteAllBytes(Path.Combine(evidenceDirectory, "native-loaded-" + owned.Id + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(new { path, requested, SdkAssembly = typeof(IExtension).Assembly.FullName }));
    }

    private sealed class Module(nint handle, string directory, string id) : IAsyncDisposable
    {
        private nint _handle = handle;
        internal string Id => id;
        public ValueTask DisposeAsync()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
                File.WriteAllText(Path.Combine(directory, "native-closed-" + id), "closed");
            }
            return ValueTask.CompletedTask;
        }
    }
}
