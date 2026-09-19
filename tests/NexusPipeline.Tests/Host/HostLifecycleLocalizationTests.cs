using System.Collections.Specialized;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using NexusPipeline.Host.Initialization;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Shared.Localization;
namespace NexusPipeline.Tests.Host;


public sealed class HostLifecycleLocalizationTests
{

    [Fact]
    public void HostLifecycleEnglishMessagesDoNotLeakChinese()
    {
        string[] messages =
        {
            RuntimeInitializer.AdministratorRequiredMessage("en-US"),
            RuntimeInitializer.AdministratorRequiredTitle("en-US"),
            RuntimeInitializer.LimitsFatalMessage("en-US"),
            RuntimeInitializer.LocalizeLimitFatal(
                "约束配置 [MaxScripts（脚本实例上限）=1000] 超出警告区间（允许 1-999），禁止启动",
                "en-US"),
            Bootstrap.LocalizeExitReason(Bootstrap.ActiveRunsReason, "en-US"),
            Bootstrap.LocalizeExitReason(Bootstrap.ConfigEditSessionsReason, "en-US"),
            Bootstrap.LocalizeExitReason(Bootstrap.PendingSystemActionReason, "en-US"),
            Bootstrap.LocalizeExitLog(
                "exit.request_rejected",
                "Exit request rejected: {reason}",
                Bootstrap.ActiveRunsReason,
                "en-US"),
        };

        foreach (string message in messages)
        {
            Assert.DoesNotMatch("[\\u3400-\\u9fff]", message);
        }
    }

    [Theory]
    [InlineData("en-US", "zh-CN", "en-US")]
    [InlineData("", "en-US", "en-US")]
    [InlineData("fr-FR", "en-GB", "en-US")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    public void ResolveEarlyHostLocalePrefersConfiguredLocaleAndUsesSystemFallback(
        string configuredLocale,
        string uiLocale,
        string expected)
    {
        Assert.Equal(expected, RuntimeInitializer.ResolveEarlyHostLocale(configuredLocale, uiLocale));
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
