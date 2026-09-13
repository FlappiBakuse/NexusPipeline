using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusPipeline.Utilities;

/// <summary>宿主与插件共用的受限版本阶段。</summary>
internal enum NexusVersionStage
{
    Beta = 0,
    Rc = 1,
    Stable = 2,
}

/// <summary>
/// NexusPipeline 当前版本格式：major.minor.patch、-beta.N 或 -rc.N。
/// 比较顺序先比较三段核心版本，再比较预发布阶段和阶段序号。
/// </summary>
internal readonly record struct NexusVersion(
    int Major,
    int Minor,
    int Patch,
    NexusVersionStage Stage,
    int StageNumber) : IComparable<NexusVersion>
{
    private static readonly Regex Pattern = new(
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-(beta|rc)\\.(0|[1-9]\\d*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Compiled);

    /// <summary>版本字符串是否带有 beta/rc 预发布后缀。</summary>
    public bool HasPrereleaseSuffix => Stage != NexusVersionStage.Stable;

    public static NexusVersion Stable(int major, int minor, int patch) =>
        new(major, minor, patch, NexusVersionStage.Stable, 0);

    public static bool TryParse(string? value, out NexusVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Match match = Pattern.Match(value.Trim());
        if (!match.Success
            || !TryParsePart(match.Groups[1].Value, out int major)
            || !TryParsePart(match.Groups[2].Value, out int minor)
            || !TryParsePart(match.Groups[3].Value, out int patch))
        {
            return false;
        }

        if (!match.Groups[4].Success)
        {
            version = Stable(major, minor, patch);
            return true;
        }

        if (!TryParsePart(match.Groups[5].Value, out int stageNumber))
        {
            return false;
        }

        NexusVersionStage stage = string.Equals(match.Groups[4].Value, "beta", StringComparison.Ordinal)
            ? NexusVersionStage.Beta
            : NexusVersionStage.Rc;
        version = new NexusVersion(major, minor, patch, stage, stageNumber);
        return true;
    }

    public static bool TryParseTag(string? value, out NexusVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }
        return TryParse(text, out version);
    }

    public int CompareTo(NexusVersion other)
    {
        int core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }
        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }
        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }
        core = Stage.CompareTo(other.Stage);
        return core != 0 ? core : StageNumber.CompareTo(other.StageNumber);
    }

    public override string ToString()
    {
        string core = string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");
        return Stage switch
        {
            NexusVersionStage.Beta => string.Create(CultureInfo.InvariantCulture, $"{core}-beta.{StageNumber}"),
            NexusVersionStage.Rc => string.Create(CultureInfo.InvariantCulture, $"{core}-rc.{StageNumber}"),
            _ => core,
        };
    }

    private static bool TryParsePart(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
