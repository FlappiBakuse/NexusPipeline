using System.Diagnostics;
using NexusPipeline.Core.Configuration;
using System.Runtime.InteropServices;
using NexusPipeline.Platform.Storage.Journals;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledLibraryTests
{
    [Fact(DisplayName = "An Actually Installed Declared Native Library Is Loaded And Released Across Two Original Inventory Activations")]
    public async Task OwnsNativeMappings()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "native");
        var owner = fixture.Package.Catalog;
        var first = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(first.IsEnabled);
        var native = Path.Combine(fixture.Target, "native", "nexus-native-fixture.dll");
        Assert.Contains(Modules(), path => StringComparer.OrdinalIgnoreCase.Equals(path, native));
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        using (var root = owner.Resources.TryAcquire(Guid.NewGuid(), [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease)
        {
            Assert.NotNull(root);
            using var stop = await fixture.Activator.ReserveForApplyAsync(root, "fixture-plugin", default);
            Assert.DoesNotContain(Modules(), path => StringComparer.OrdinalIgnoreCase.Equals(path, native));
            var moved = native + ".closed";
            File.Move(native, moved); File.Move(moved, native);
        }
        var second = await new InstalledActivationService(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, fixture.Roots, owner.Resources, owner.Index, owner.Store, fixture.Activator)
            .ActivateAsync("fixture-plugin", default);
        Assert.NotEqual(first.ActivationId, second.ActivationId);
        Assert.Contains(Modules(), path => StringComparer.OrdinalIgnoreCase.Equals(path, native));
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Modules(), path => StringComparer.OrdinalIgnoreCase.Equals(path, native));
        var results = Directory.EnumerateFiles(fixture.ActivationDirectory, "native-loaded-*.json").ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(2, Directory.EnumerateFiles(fixture.ActivationDirectory, "native-closed-*").Count());
        foreach (var result in results)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(result));
            Assert.Equal(native, json.RootElement.GetProperty("path").GetString());
            Assert.Equal(typeof(IExtension).Assembly.FullName, json.RootElement.GetProperty("SdkAssembly").GetString());
        }
    }

    [Theory(DisplayName = "The Actual Installed Loader Rejects Undeclared Basename Outside And Managed Native Requests")]
    [InlineData("native-undeclared")]
    [InlineData("native-basename")]
    [InlineData("native-outside")]
    [InlineData("native-managed")]
    public async Task RejectsUndeclaredLibraries(string mode)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: mode);
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
        var expected = mode == "native-managed" ? "extension_native_image_invalid" : "extension_native_library_not_declared";
        Assert.Contains(Flatten(error), item => item.Message == expected);
        Assert.Empty(Directory.EnumerateFiles(fixture.ActivationDirectory, "native-loaded-*.json"));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        Assert.DoesNotContain(Modules(), path => path.StartsWith(fixture.Target + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) && path.EndsWith("nexus-native-fixture.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Null(fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit);
    }

    [Fact(DisplayName = "A Same Path Native Mapping Without This Inventorys Original Witness Cannot Be Adopted")]
    public async Task RejectsUnownedMappings()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "native");
        var observer = new UnownedNativeMapping(Path.Combine(fixture.Target, "native", "nexus-native-fixture.dll"));
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session(observer).ApplyAsync(fixture.Prepared, default));
            Assert.NotEqual(nint.Zero, observer.Module);
            Assert.Contains(Flatten(failure), error => error.Message == "extension_native_module_conflict");
            Assert.Empty(Directory.EnumerateFiles(fixture.ActivationDirectory, "native-loaded-*.json"));
            Assert.Empty(fixture.Activator.ReadPublishedScopes());
            Assert.Null(fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit);
        }
        finally { if (observer.Module != 0) NativeLibrary.Free(observer.Module); }
    }

    private sealed class UnownedNativeMapping(string path) : IPayloadPersistenceObserver
    {
        internal nint Module;
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point == PayloadPersistencePoint.PhysicalTreeMoved && Module == 0 && File.Exists(path))
                Module = NativeLibrary.Load(path, typeof(UnownedNativeMapping).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        }
    }

    private static string[] Modules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Select(module => Path.GetFullPath(module.FileName)).ToArray();
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var nested in Flatten(inner)) yield return nested;
        else if (error.InnerException is { } inner)
            foreach (var nested in Flatten(inner)) yield return nested;
    }
}
