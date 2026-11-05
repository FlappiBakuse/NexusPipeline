using System.Text.RegularExpressions;

namespace NexusPipeline.Core.Maintenance;

public static class AutomaticUpdatePolicy
{
    public static TimeSpan FirstCheckDelay => TimeSpan.FromSeconds(5);
    public static TimeSpan CheckInterval => TimeSpan.FromHours(12);
    private static readonly Regex VersionShape = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(beta|rc)\.([1-9][0-9]*))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static int CompareVersions(string left, string right)
    {
        var a = Parse(left); var b = Parse(right);
        for (var index = 1; index <= 3; index++)
        {
            var comparison = Number(a.Groups[index].Value, b.Groups[index].Value);
            if (comparison != 0) return comparison;
        }
        var rank = Rank(a.Groups[4].Value).CompareTo(Rank(b.Groups[4].Value));
        return rank != 0 ? rank : Number(a.Groups[5].Value, b.Groups[5].Value);
    }

    public static bool ShouldApplyPending(HostUpdateTiming timing, bool presentAtStartup, bool startupPass)
        => timing == HostUpdateTiming.NextStartup ? presentAtStartup && startupPass :
            timing == HostUpdateTiming.WhenIdle && !startupPass;

    public static bool ShouldUpgradeExtension(string candidateVersion, string installedVersion, bool identicalContent,
        bool candidateIsDevelop, bool installedFromDevelop)
    {
        if (identicalContent) return false;
        var comparison = CompareVersions(candidateVersion, installedVersion);
        return comparison > 0 || comparison == 0 && (candidateIsDevelop || installedFromDevelop);
    }

    private static Match Parse(string value)
    {
        if (value is not { Length: >= 1 and <= 80 }) throw new ArgumentException("update_version_invalid");
        var match = VersionShape.Match(value);
        return match.Success ? match : throw new ArgumentException("update_version_invalid");
    }
    private static int Rank(string value) => value switch { "beta" => 0, "rc" => 1, "" => 2, _ => throw new ArgumentException("update_version_invalid") };
    private static int Number(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : StringComparer.Ordinal.Compare(left, right);
}
