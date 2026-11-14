using System.Buffers.Binary;
using System.Text;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class DatabaseJournals
{
    private static readonly byte[] Magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool Known(string name) => DatabaseLayout.Domains.Any(domain =>
        name == DatabaseLayout.Name(domain) + ".db" || name == DatabaseLayout.Name(domain) + ".db-journal") || SuperName(name);

    private static bool SuperName(string name) => name.StartsWith("runtime.db-mj", StringComparison.Ordinal) &&
        name.Length is > 13 and <= 64 && name[13..].All(char.IsAsciiHexDigit);

    internal static IReadOnlyList<string> Inventory(string coordinator)
    {
        DatabaseLayout.RequireFiles(coordinator);
        var root = Path.GetDirectoryName(coordinator)!;
        var preferences = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, InterfacePreferences.FileName));
        if (Directory.Exists(preferences)) throw new IOException("database_group_unknown_entry");
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
            .Where(name => name != InterfacePreferences.FileName).ToArray();
        if (names.Any(name => name is null || !Known(name))) throw new IOException("database_group_unknown_entry");
        return names.Select(name => name!).Order(StringComparer.Ordinal).ToArray();
    }

    internal static void VerifyPaths(string coordinator)
    {
        var root = Path.GetDirectoryName(coordinator)!;
        foreach (var name in Inventory(coordinator).Where(name => name.EndsWith("-journal", StringComparison.Ordinal)))
        {
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(root, name));
            if (ReadSuper(path) is { } parent)
            {
                RequireMember(root, parent, super: true);
                var master = PlainPath(parent);
                if (File.Exists(master))
                    foreach (var member in ReadMembers(master)) RequireMember(root, member, super: false);
            }
        }
    }

    internal static void RebindCopy(string sourceRoot, string copyRoot)
    {
        var references = Directory.EnumerateFiles(copyRoot, "*-journal").Select(ReadSuper).OfType<string>()
            .Select(parent => Path.GetFileName(PlainPath(parent))).ToHashSet(StringComparer.Ordinal);
        Span<byte> trailer = stackalloc byte[16];
        foreach (var path in Directory.EnumerateFiles(copyRoot))
        {
            var name = Path.GetFileName(path);
            if (SuperName(name) && references.Contains(name))
            {
                var members = ReadMembers(path);
                foreach (var member in members) RequireMember(sourceRoot, member, super: false);
                File.WriteAllBytes(path, Utf8.GetBytes(string.Join('\0', members.Select(member =>
                    PhysicalPathPolicy.NativePath(Path.Combine(copyRoot, Path.GetFileName(PlainPath(member)))))) + "\0"));
            }
            else if (name.EndsWith("-journal", StringComparison.Ordinal) && ReadSuper(path) is { } parent)
            {
                RequireMember(sourceRoot, parent, super: true);
                var replacement = Utf8.GetBytes(PhysicalPathPolicy.NativePath(Path.Combine(copyRoot, Path.GetFileName(PlainPath(parent)))));
                using var journal = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                journal.Position = journal.Length - 16; journal.ReadExactly(trailer);
                var length = BinaryPrimitives.ReadUInt32BigEndian(trailer);
                journal.Position = journal.Length - 16 - length;
                journal.Write(replacement);
                BinaryPrimitives.WriteUInt32BigEndian(trailer, (uint)replacement.Length);
                BinaryPrimitives.WriteUInt32BigEndian(trailer[4..], Checksum(replacement));
                journal.Write(trailer); journal.SetLength(journal.Position); journal.Flush(flushToDisk: true);
            }
        }
    }

    private static string PlainPath(string path) => path.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)
        ? "\\\\" + path[8..] : path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;

    private static void RequireMember(string root, string member, bool super)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(PlainPath(member));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), root) ||
            (super ? !SuperName(Path.GetFileName(path)) : !DatabaseLayout.Domains.Any(domain =>
                Path.GetFileName(path) == DatabaseLayout.Name(domain) + ".db-journal")))
            throw new IOException("database_journal_path_unrecognized");
    }

    private static string[] ReadMembers(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is < 1 or > 1048576) throw new IOException("database_super_journal_invalid");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (bytes[^1] != 0) throw new IOException("database_super_journal_invalid");
        var names = Utf8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length is < 1 or > 8 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new IOException("database_super_journal_invalid");
        return names;
    }

    private static string? ReadSuper(string path)
    {
        using var input = File.OpenRead(path);
        if (!VerifyHeader(input)) return null;
        if (input.Length < 16) return null;
        Span<byte> trailer = stackalloc byte[16]; input.Position = input.Length - 16; input.ReadExactly(trailer);
        if (!trailer[8..].SequenceEqual(Magic)) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(trailer);
        if (length is 0 or > 131072 || length > input.Length - 16) throw new IOException("database_super_journal_invalid");
        var bytes = new byte[(int)length]; input.Position = input.Length - 16 - length; input.ReadExactly(bytes);
        if (Checksum(bytes) != BinaryPrimitives.ReadUInt32BigEndian(trailer[4..])) throw new IOException("database_super_journal_invalid");
        return Utf8.GetString(bytes);
    }

    private static bool VerifyHeader(Stream input)
    {
        if (input.Length == 0) return false;
        Span<byte> header = stackalloc byte[28];
        var count = input.Read(header);
        if (header[..Math.Min(count, 8)].IndexOfAnyExcept((byte)0) < 0) return false;
        if (count < header.Length || !header[..8].SequenceEqual(Magic))
            throw new IOException("database_rollback_journal_unrecognized");
        var sector = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var page = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        if (sector is < 32 or > 65536 || (sector & (sector - 1)) != 0 ||
            page is < 512 or > 65536 || (page & (page - 1)) != 0)
            throw new IOException("database_rollback_journal_unrecognized");
        return true;
    }

    private static uint Checksum(byte[] bytes)
    {
        uint result = 0;
        foreach (var value in bytes) result = unchecked(result + (uint)(sbyte)value);
        return result;
    }
}
