namespace NexusPipeline.Platform.Storage;

internal static class PhysicalPathPolicy
{
    internal static string RuntimeApplicationDirectory()
    {
        var path = AppContext.BaseDirectory;
        // A native apphost can give the CLR a Win32-prefixed base directory; external inputs stay strict.
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) && path.Length >= 7 &&
            char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') path = path[4..];
        return NormalizeWithoutLinks(Path.TrimEndingDirectorySeparator(path));
    }

    internal static string NativePath(string path)
    {
        var full = NormalizeSyntax(path);
        return full.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + full[2..] : "\\\\?\\" + full;
    }

    private sealed record SyntaxPath(string Full, string[] Ancestors, int Characters);
    private const int MaximumSyntaxEntries = 256;
    private const int MaximumSyntaxCharacters = 256 * 1024;
    private static readonly object SyntaxGate = new();
    private static readonly Dictionary<string, SyntaxPath> SyntaxPaths = new(StringComparer.Ordinal);
    private static readonly Queue<string> SyntaxOrder = new();
    private static int _syntaxCharacters;

    internal static string NormalizeWithoutLinks(string path)
    {
        var syntax = ReadSyntax(path);
        // Attribute scopes pin directory names; their handles still supply current attributes on every call.
        foreach (var current in syntax.Ancestors)
        {
            try
            {
                var attributes = DirectoryAttributeScope.TryReadCurrent(current, out var held) ? held : File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("filesystem_reparse_point_rejected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return syntax.Full;
    }

    internal static string NormalizeSyntax(string path) => ReadSyntax(path).Full;

    internal static string NormalizeParent(string path)
    {
        var full = NormalizeSyntax(path);
        NormalizeWithoutLinks(Path.GetDirectoryName(full) ?? throw new IOException("filesystem_file_path_required"));
        return full;
    }

    private static SyntaxPath ReadSyntax(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (SyntaxGate) if (SyntaxPaths.TryGetValue(path, out var cached)) return cached;
        var full = ValidateSyntax(path);
        var ancestors = new List<string>();
        long characters = path.Length + (long)full.Length;
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        { ancestors.Add(current); characters += current.Length; }
        var value = new SyntaxPath(full, ancestors.ToArray(), checked((int)Math.Min(characters, int.MaxValue)));
        if (characters > MaximumSyntaxCharacters) return value;
        lock (SyntaxGate)
        {
            if (SyntaxPaths.TryGetValue(path, out var cached)) return cached;
            while (SyntaxPaths.Count >= MaximumSyntaxEntries || _syntaxCharacters + value.Characters > MaximumSyntaxCharacters)
            {
                var oldest = SyntaxOrder.Dequeue();
                _syntaxCharacters -= SyntaxPaths[oldest].Characters; SyntaxPaths.Remove(oldest);
            }
            SyntaxPaths.Add(path, value); SyntaxOrder.Enqueue(path); _syntaxCharacters += value.Characters;
        }
        return value;
    }

    private static string ValidateSyntax(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An explicit absolute filesystem path is required");
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal) || full.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            full.IndexOf(':') is var colon && colon >= 0 && (colon != 1 || full.LastIndexOf(':') != 1))
            throw new IOException("filesystem_device_or_stream_path_rejected");
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            ValidateComponent(part);
        return full;
    }

    internal static void ValidateRelativePath(string relative)
    {
        if (relative.Length > 1024 || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':'))
            throw new IOException("unsupported_snapshot_relative_path");
        foreach (var part in relative.Split('/')) ValidateComponent(part);
    }

    private static void ValidateComponent(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
            part.Any(char.IsControl) || part.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0 ||
            stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
            throw new IOException("unsupported_filesystem_component");
        for (var index = 0; index < part.Length; index++)
        {
            if (char.IsHighSurrogate(part[index]))
            {
                if (++index >= part.Length || !char.IsLowSurrogate(part[index])) throw new IOException("invalid_path_unicode");
            }
            else if (char.IsLowSurrogate(part[index])) throw new IOException("invalid_path_unicode");
        }
    }
}
