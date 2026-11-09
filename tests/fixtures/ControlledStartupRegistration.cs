using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Testing;

internal sealed class ControlledStartupRegistration : IStartupRegistrationEffects
{
    internal string Path { get; }
    internal int Deletes;
    internal Action? BeforeCreate { get; set; }
    internal Action? BeforeMutation { get; set; }
    internal bool FailValidation { get; set; }
    internal bool RequireElevation { get; set; }
    internal bool ThrowAfterCreate { get; set; }

    internal ControlledStartupRegistration(string path)
    {
        var root = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("fixture_root_required"));
        Path = System.IO.Path.GetFullPath(path);
        if (!Path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("startup_fixture_path_outside_test_root");
        for (var parent = Path; parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
            if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("startup_fixture_link_rejected");
    }

    public StartupRegistrationInspection Inspect(StartupRegistrationDefinition expected)
    {
        if (!File.Exists(Path)) return new(StartupRegistrationState.Missing, null);
        var actual = File.ReadAllText(Path);
        return new(actual == StartupTaskXml.Encode(expected) ? StartupRegistrationState.Matches : StartupRegistrationState.Foreign,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
    }

    public void ValidateOnly(StartupRegistrationDefinition definition)
    {
        if (RequireElevation) throw new HostSettingsException("startup_registration_elevation_required");
        if (FailValidation) throw new IOException("fixture_validate_failed");
    }

    public StartupRegistrationEffect CreateIfMissing(StartupRegistrationCommand command)
    {
        BeforeMutation?.Invoke();
        BeforeCreate?.Invoke();
        using (var file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            using var writer = new StreamWriter(file, Encoding.UTF8, 4096, leaveOpen: true);
            writer.Write(StartupTaskXml.Encode(command.Definition));
            writer.Flush();
            file.Flush(flushToDisk: true);
        }
        if (ThrowAfterCreate) throw new IOException("fixture_native_outcome_unknown");
        return StartupRegistrationEffect.Created;
    }

    public StartupRegistrationEffect RemoveIfMatching(StartupRegistrationDefinition expected)
    {
        BeforeMutation?.Invoke();
        if (Inspect(expected).State != StartupRegistrationState.Matches) throw new IOException("fixture_not_owned");
        File.Delete(Path);
        Deletes++;
        return StartupRegistrationEffect.Removed;
    }
}
