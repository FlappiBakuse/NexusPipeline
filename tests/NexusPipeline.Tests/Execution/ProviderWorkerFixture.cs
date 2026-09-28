namespace NexusPipeline.Tests.Execution;

/// <summary>把实际构建的 TestProviderWorker 复制到独立临时目录，供 provider 执行链路用例使用。</summary>
internal sealed class ProviderWorkerFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "nxp-provider-worker-" + Guid.NewGuid().ToString("N"));

    internal ProviderWorkerFixture()
    {
        Directory.CreateDirectory(Root);
        string repository = FindRoot();
        string configuration = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar)
            .Last(part => part is "Debug" or "Release");
        bool testHost = AppContext.BaseDirectory.StartsWith(
            Path.Combine(repository, "bin", "test-host") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        string source = testHost
            ? Path.Combine(repository, "bin", "test-host", "NexusPipeline.TestProviderWorker", configuration, "net8.0-windows")
            : Path.Combine(repository, "tests", "fixtures", "NexusPipeline.TestProviderWorker", "bin", configuration, "net8.0-windows");
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        }
    }

    private static string FindRoot()
    {
        for (string? path = AppContext.BaseDirectory; path is not null; path = Path.GetDirectoryName(path))
        {
            if (File.Exists(Path.Combine(path, "src", "NexusPipeline.csproj")))
            {
                return path;
            }
        }
        throw new InvalidOperationException("test repository not found");
    }

    public void Dispose() => Directory.Delete(Root, true);
}
