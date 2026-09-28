using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using NexusPipeline.Modules.History.Localization;
using NexusPipeline.Modules.History;
using NexusPipeline.Shared.Localization;
namespace NexusPipeline.Tests.History;


public sealed class RunResultLocalizationTests
{

    [Fact]
    public void RunResultLocalization_ProjectsStableResultArgsPerLocale()
    {
        var record = new RunRecord
        {
            ResultCode = "run.daily_cap",
            ResultDetail = "达到每日成功运行次数上限（2/3），本次跳过",
            ResultArgs = new Dictionary<string, string>
            {
                ["successful"] = "2",
                ["maximum"] = "3",
            },
        };

        Assert.Equal(record.ResultDetail, RunResultLocalization.Detail(record, "zh-CN"));
        Assert.Equal(
            "The daily success limit was reached (2/3); this run was skipped",
            RunResultLocalization.Detail(record, "en-US"));
    }

    [Fact]
    public void UnverifiedTaskOutcomeIncludesFrozenUnknownCountWithoutClaimingSuccess()
    {
        var record = new RunRecord
        {
            Status = "partial",
            ResultCode = "tasks_unverified",
            ResultDetail = "incomplete",
            TaskReport = JsonNode.Parse("""{"summary":{"counts":{"unknown":2}}}""")!.AsObject(),
        };
        Assert.Equal("流程已结束 · 有 2 项未核验", RunResultLocalization.Detail(record, "zh-CN"));
        Assert.Equal("Run ended · 2 item(s) unverified", RunResultLocalization.Detail(record, "en-US"));
    }

    private static Dictionary<string, string> ReadEmbeddedResource(string locale)
    {
        Assembly assembly = typeof(HostLocalization).Assembly;
        string suffix = $".Localization.Resources.{locale}.json";
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Invalid embedded resource: {resourceName}");
    }

    private static string[] Placeholders(string value)
    {
        return Regex.Matches(value, @"\{([A-Za-z][A-Za-z0-9_.-]*)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }
}
