using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;

internal static class GamePluginFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var inputPath, var reportPath]) return 2;
        var canary = Canary();
        var reportValidated = false;
        try
        {
            OwnedFixtureEvidence.RequirePath(inputPath);
            OwnedFixtureEvidence.RequirePath(reportPath);
            reportValidated = true;
            using var input = StrictJson.Parse(File.ReadAllBytes(inputPath));
            var settings = input.RootElement;
            var assembly = settings.GetProperty("assembly").GetString()!;
            var manifest = settings.GetProperty("manifest").GetString()!;
            OwnedFixtureEvidence.RequirePath(assembly);
            OwnedFixtureEvidence.RequirePath(manifest);
            var invocation = new FrozenScheduledInvocation(settings.GetProperty("instanceId").GetGuid(), settings.GetProperty("occurrenceId").GetGuid(),
                settings.GetProperty("runId").GetGuid(), settings.GetProperty("attemptId").GetGuid(), settings.GetProperty("subjectId").GetGuid(),
                settings.GetProperty("notificationOperationId").GetGuid(), DateTimeOffset.UtcNow.AddMinutes(3),
                settings.GetProperty("notificationTaskIds").EnumerateArray().Select(item => item.GetGuid()).ToArray());
            using var plugin = new PluginSliceAssembly(assembly, settings.GetProperty("assemblySha256").GetString()!, manifest, "game-checkin", new());
            var result = await new GamePluginProof(plugin, invocation).RunAsync(canary);
            OwnedFixtureEvidence.WriteReport(reportPath, result);
            VerifyPlaintextAbsence(Path.GetDirectoryName(reportPath)!, canary);
            return 0;
        }
        catch (Exception error)
        {
            if (reportValidated && !File.Exists(reportPath))
                OwnedFixtureEvidence.WriteReport(reportPath, new { status = "FAIL", type = error.GetType().FullName, code = error.Message });
            return 1;
        }
        finally { Array.Clear(canary); }
    }

    private static char[] Canary()
    {
        var random = RandomNumberGenerator.GetBytes(32);
        var chars = new char[72];
        "fixture=".AsSpan().CopyTo(chars);
        for (var index = 0; index < random.Length; index++) random[index].TryFormat(chars.AsSpan(8 + index * 2, 2), out _, "x2");
        CryptographicOperations.ZeroMemory(random);
        return chars;
    }

    private static void VerifyPlaintextAbsence(string root, ReadOnlySpan<char> plaintext)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        Encoding.UTF8.GetBytes(plaintext, bytes);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                OwnedFixtureEvidence.RequirePath(path);
                if (new FileInfo(path).Length > StrictJson.MaximumBytes) throw new IOException("checkin_fixture_evidence_file_limit");
                if (File.ReadAllBytes(path).AsSpan().IndexOf(bytes) >= 0) throw new IOException("checkin_fixture_plaintext_file_detected");
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
