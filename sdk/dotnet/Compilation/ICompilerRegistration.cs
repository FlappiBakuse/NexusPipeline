namespace NexusPipeline.Extensibility.Compilation;

/// <summary>Only manifest-declared compilers are visible after their owning activation is published.</summary>
public interface ICompilerRegistration
{
    /// <summary>The Host owns registration cleanup and drains callbacks with the actual activation.</summary>
    ValueTask RegisterAsync(string compilerId, IProjectCompiler compiler, CancellationToken cancellationToken);
}
