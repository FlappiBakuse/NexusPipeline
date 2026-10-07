using System.Security.Cryptography;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.ControlPlane.Cli;

internal static class CliArtifactOutput
{
    private const int MaxBytes = 8 * 1024 * 1024;
    internal static string ValidateDestination(string destination)
    {
        string full = Path.GetFullPath(destination);
        PayloadPathSafety.RequireLinkFree(full);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("输出目标已存在，不能覆盖");
        if (Path.GetDirectoryName(full) is not { } parent || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("输出目录不存在");
        return full;
    }

    internal static async Task SaveAsync(Stream source, string destination, long sizeBytes, string sha256, CancellationToken token = default)
    {
        if (sizeBytes is < 0 or > MaxBytes || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException("诊断包回执无效");
        string full = ValidateDestination(destination);
        using var output = new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > sizeBytes) throw new InvalidDataException("诊断包大小与回执不符");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
            }
            if (written != sizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != sha256)
                throw new InvalidDataException("诊断包摘要与回执不符");
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                output.Flush(); output.Position = 0;
                long size = output.Length;
                string hash = Convert.ToHexStringLower(SHA256.HashData(output));
                output.Dispose();
                VerifiedFileDeletion.Delete(full, size, hash);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { NexusPipeline.Shared.Logging.Logger.Warn("diagnostics_partial_output_preserved: " + cleanup.GetType().Name); }
            throw;
        }
    }
}
