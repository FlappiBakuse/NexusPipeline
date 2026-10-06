using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Host.Desktop;

internal sealed record DesktopSessionRecord(int SchemaVersion, string RootHash, string Generation, string SessionId, string BuildId,
    string HostInstanceId, DesktopProcessIdentity Main, string WindowState, string Route, string HandoffId)
{
    public IReadOnlyList<DesktopProcessIdentity> Family { get; init; } = [];
    public int MainRestartCount { get; init; }
}
internal sealed class DesktopSessionStore(string directory, string rootHash)
{
    private readonly object _gate = new();
    private string RecordPath => Path.Combine(directory, "session.json");
    private string KeyPath => Path.Combine(directory, "session.key");
    internal bool HasFiles => File.Exists(RecordPath) || File.Exists(KeyPath);
    internal (DesktopSessionRecord Record, byte[] Key)? Load()
    {
        lock (_gate)
        {
        if (!HasFiles) return null;
        try
        {
            GuardDirectory();
            if (!File.Exists(RecordPath) || !File.Exists(KeyPath) || new FileInfo(RecordPath).Length > 16384 || new FileInfo(KeyPath).Length != 32) return null;
            RejectLink(RecordPath); RejectLink(KeyPath);
            byte[] key = File.ReadAllBytes(KeyPath);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(RecordPath));
            JsonElement value = document.RootElement;
            DesktopSupervisorProtocol.Fields(value, "record", "signature");
            DesktopSupervisorProtocol.Fields(value.GetProperty("record"), "schemaVersion", "rootHash", "generation", "sessionId", "buildId", "hostInstanceId", "main", "windowState", "route", "handoffId", "family", "mainRestartCount");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value.GetProperty("record"), DesktopPipeTransport.Json);
            string expected = Convert.ToHexString(HMACSHA256.HashData(key, bytes)).ToLowerInvariant();
            if (!DesktopSupervisorProtocol.EqualsProof(value.GetProperty("signature").GetString()!, expected)) return null;
            JsonElement body = value.GetProperty("record");
            DesktopSupervisorProtocol.Fields(body.GetProperty("main"), "pid", "startFileTime", "executablePath");
            if (body.GetProperty("family").ValueKind != JsonValueKind.Array) return null;
            foreach (JsonElement member in body.GetProperty("family").EnumerateArray())
                DesktopSupervisorProtocol.Fields(member, "pid", "startFileTime", "executablePath");
            DesktopSessionRecord record = value.GetProperty("record").Deserialize<DesktopSessionRecord>(DesktopPipeTransport.Json)!;
            if (record is null || record.SchemaVersion != 1 || record.RootHash != rootHash || record.Generation != "g0170"
                || !Guid.TryParseExact(record.SessionId, "N", out _) || record.BuildId.Length != 64 || !record.BuildId.All(char.IsAsciiHexDigitLower)
                || record.Main is null || !ValidProcess(record.Main) || !Guid.TryParseExact(record.HostInstanceId, "N", out _)
                || record.WindowState is not ("hidden" or "visible" or "minimized") || record.Route.Length > 256
                || record.Family is null || record.Family.Count > 128 || record.Family.Any(member => !ValidProcess(member))
                || record.Family.Select(member => member.Pid).Distinct().Count() != record.Family.Count
                || record.MainRestartCount is < 0 or > 1 || record.HandoffId != "" && !Guid.TryParseExact(record.HandoffId, "N", out _)
                || !System.Text.RegularExpressions.Regex.IsMatch(record.Route, "^#/[a-zA-Z0-9_/-]*$")) return null;
            return (record, key);
        }
        catch { return null; }
            }
    }
    internal byte[] CreateKey()
    {
        lock (_gate)
        {
        if (HasFiles) throw new InvalidDataException("desktop_session_unowned");
        SafeDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using (var stream = new FileStream(KeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(key);
        PrivateFile(KeyPath);
        return key;
            }
    }
    internal void Save(DesktopSessionRecord record, byte[] key)
    {
        lock (_gate)
        {
        SafeDirectory();
        RejectLink(KeyPath);
        if (key.Length != 32 || !CryptographicOperations.FixedTimeEquals(File.ReadAllBytes(KeyPath), key)) throw new InvalidDataException("desktop_session_unowned");
        if (File.Exists(RecordPath))
        {
            var current = Load();
            if (current is null || current.Value.Record.SessionId != record.SessionId) throw new InvalidDataException("desktop_session_unowned");
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, DesktopPipeTransport.Json);
        string signature = Convert.ToHexString(HMACSHA256.HashData(key, bytes)).ToLowerInvariant();
        string temporary = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { record, signature }, DesktopPipeTransport.Json));
        PrivateFile(temporary);
        if (File.Exists(RecordPath)) RejectLink(RecordPath);
        File.Move(temporary, RecordPath, true);
            }
    }
    internal void RemoveOwned(DesktopSessionRecord record, byte[] key)
    {
        lock (_gate)
        {
        var current = Load();
        if (current is null || current.Value.Record.SessionId != record.SessionId || !record.Main.HasExited()
            || current.Value.Record.Family.Any(identity => !identity.HasExited())
            || !CryptographicOperations.FixedTimeEquals(current.Value.Key, key)) return;
        File.Delete(RecordPath);File.Delete(KeyPath);
            }
    }
    private static bool ValidProcess(DesktopProcessIdentity identity) => identity is not null && identity.Pid > 0
        && long.TryParse(identity.StartFileTime, out long start) && start > 0 && Path.IsPathFullyQualified(identity.ExecutablePath);
    private void SafeDirectory()
    {
        GuardDirectory();
        Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();security.SetAccessRuleProtection(true, false);
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
    private void GuardDirectory()
    {
        for (string? current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current)) RejectLink(current);
    }
    private static void PrivateFile(string file)
    {
        var security = new FileSecurity();security.SetAccessRuleProtection(true, false);
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
    private static void RejectLink(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked desktop session"); }
}
