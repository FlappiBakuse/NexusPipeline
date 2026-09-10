using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusPipeline.Localization;
using NexusPipeline.Models;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hant-TW", "zh-CN")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "zh-CN")]
    [InlineData("", "zh-CN")]
    public void Normalize_UsesOnlySupportedLocales(string input, string expected)
    {
        Assert.Equal(expected, LocaleCatalog.Normalize(input));
    }

    [Fact]
    public void Resolve_PrefersExplicitHeaderAndFallsBackSafely()
    {
        var headers = new NameValueCollection
        {
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["X-Nexus-Locale"] = "zh-CN",
        };

        Assert.Equal("zh-CN", LocaleCatalog.Resolve(headers));

        headers.Remove("X-Nexus-Locale");
        Assert.Equal("en-US", LocaleCatalog.Resolve(headers));

        headers["Accept-Language"] = "fr-FR,ja;q=0.8";
        Assert.Equal("zh-CN", LocaleCatalog.Resolve(headers));
    }

    [Fact]
    public void HostLocalization_FormatsEnglishMessagesWithoutChangingChineseFallback()
    {
        Assert.Equal(
            "Plugin not found: GameCheckIn",
            HostLocalization.Translate("plugin.not_found", "未找到插件：GameCheckIn", "en-US", new object?[] { "GameCheckIn" }));
        Assert.Equal(
            "未找到插件：GameCheckIn",
            HostLocalization.Translate("plugin.not_found", "未找到插件：GameCheckIn", "zh-CN", new object?[] { "GameCheckIn" }));
    }

    [Fact]
    public void CliLocalization_UsesStableKeysAndNamedPlaceholders()
    {
        Assert.Equal(
            "NexusPipeline management menu",
            HostLocalization.TranslateNamed("cli.menu.main_title", "fallback", locale: "en-US"));
        Assert.Equal(
            "Missing --name (user name)",
            HostLocalization.TranslateNamed(
                "cli.error.missing_option",
                "fallback",
                new Dictionary<string, object?>
                {
                    ["name"] = "name",
                    ["label"] = "user name",
                },
                "en-US"));
        Assert.Equal(
            "NexusPipeline 枢链 管理菜单",
            HostLocalization.TranslateNamed("cli.menu.main_title", "fallback", locale: "zh-CN"));
    }

    [Fact]
    public void HostLocaleResources_KeepSortedParityAndPlaceholderContracts()
    {
        Dictionary<string, string> zh = ReadEmbeddedResource("zh-CN");
        Dictionary<string, string> en = ReadEmbeddedResource("en-US");

        Assert.Equal(zh.Keys.OrderBy(key => key, StringComparer.Ordinal), zh.Keys);
        Assert.Equal(zh.Keys.OrderBy(key => key, StringComparer.Ordinal), en.Keys);
        foreach (string key in zh.Keys)
        {
            Assert.Matches("^[A-Za-z][A-Za-z0-9_.-]*$", key);
            Assert.DoesNotMatch("^(?:ui|legacy)\\.", key);
            Assert.False(string.IsNullOrWhiteSpace(zh[key]));
            Assert.False(string.IsNullOrWhiteSpace(en[key]));
            Assert.Equal(Placeholders(zh[key]), Placeholders(en[key]));
        }
    }

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
    public void HostLocalization_ProjectsHostLogTextAtTheOutputBoundary()
    {
        const string message = "[警告] 解析 settings.json 失败，原文件已保留为 settings.json.bak";

        string english = HostLocalization.TranslateLog(message, "en-US");
        Assert.Equal("[Warning]", HostLocalization.Translate("log.token.warning", "fallback", "en-US"));
        Assert.Equal("parse", HostLocalization.Translate("log.token.parse", "fallback", "en-US"));
        Assert.Equal("failed", HostLocalization.Translate("log.token.failed", "fallback", "en-US"));
        Assert.Equal("original file kept as", HostLocalization.Translate("log.token.original_file_kept", "fallback", "en-US"));

        Assert.Equal("[Warning] parse settings.json failed, original file kept as settings.json.bak", english);
        Assert.DoesNotContain("解析", english, StringComparison.Ordinal);
        Assert.DoesNotContain("失败", english, StringComparison.Ordinal);
        Assert.Contains("settings.json.bak", english, StringComparison.Ordinal);
        Assert.Equal(message, HostLocalization.TranslateLog(message, "zh-CN"));

        string fallback = HostLocalization.TranslateLog("宿主固定日志文本未登记", "en-US");
        Assert.StartsWith("Host log event ", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("宿主", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("未登记", fallback, StringComparison.Ordinal);
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
