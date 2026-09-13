using System.Globalization;
using System.Text.Json;
using NexusPipeline.Localization;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Update;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline;

/// <summary>
/// 应用公共初始化：权限契约、约束加载以及只读设置快照。
/// 该阶段不加载、不修复 Scripts / Queues / Users，也不启动服务。
/// </summary>
internal static class RuntimeInitializer
{
    public static int Initialize()
    {
        InitializeEarlyHostLocale();
        if (!IsTestHost() && !IsAdministrator())
        {
            string msg = AdministratorRequiredMessage();
            Logger.Fatal(msg);
            Console.Error.WriteLine($"[FATAL] {msg}");
            try
            {
                System.Windows.Forms.MessageBox.Show(msg, AdministratorRequiredTitle(), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
            }
            return 2;
        }

        UpdateApply.CleanupWorkerImages();
        // 先加载约束，再加载设置（Normalize 使用固定的历史保留天数上限）。
        Limits.Load();
        RuntimeContext ctx = RuntimeContext.Instance;
        ctx.ReloadSettings(Persistence.ConfigLoadMode.ReadOnly);
        // 只读设置加载失败时会回退默认语言；重新读取安全的启动语言，保证后续 fatal 输出仍遵循系统语言。
        InitializeEarlyHostLocale();
        if (Limits.Fatals.Count > 0)
        {
            foreach (string fatal in Limits.Fatals)
            {
                string localizedFatal = LocalizeLimitFatal(fatal);
                Logger.Fatal(localizedFatal);
                Console.Error.WriteLine(localizedFatal);
            }
            string message = LimitsFatalMessage();
            Logger.Fatal(message);
            Console.Error.WriteLine(message);
            return 1;
        }
        foreach (string warning in Limits.Warnings)
        {
            Logger.Warn(warning);
        }
        return 0;
    }

    internal static string AdministratorRequiredMessage(string? locale = null)
    {
        return HostLocalization.TranslateNamed(
            "startup.admin_required",
            "NexusPipeline must run as administrator because script programs require administrator access. This instance did not receive administrator privileges and will exit. Right-click and choose \"Run as administrator\", or verify that the requireAdministrator build is deployed.",
            locale: locale ?? LocaleCatalog.HostLocale);
    }

    internal static string AdministratorRequiredTitle(string? locale = null)
    {
        return HostLocalization.TranslateNamed(
            "startup.admin_title",
            "NexusPipeline requires administrator privileges",
            locale: locale ?? LocaleCatalog.HostLocale);
    }

    internal static string LimitsFatalMessage(string? locale = null)
    {
        return HostLocalization.TranslateNamed(
            "startup.limits_fatal",
            "A fatal limits configuration error prevented startup. Fix config/limits.json and try again.",
            locale: locale ?? LocaleCatalog.HostLocale);
    }

    internal static string LocalizeLimitFatal(string message, string? locale = null)
    {
        return HostLocalization.TranslateLog(message, locale ?? LocaleCatalog.HostLocale);
    }

    internal static string ResolveEarlyHostLocale(string? configuredLocale, string? uiLocale)
    {
        return LocaleCatalog.TryResolve(configuredLocale, out string resolved)
            ? resolved
            : LocaleCatalog.Normalize(uiLocale);
    }

    private static void InitializeEarlyHostLocale()
    {
        string? configuredLocale = null;
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
            {
                using FileStream stream = File.OpenRead(AppPaths.ConfigPath);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(nameof(AppSettings.HostLocale), out JsonElement localeNode)
                    && localeNode.ValueKind == JsonValueKind.String)
                {
                    configuredLocale = localeNode.GetString();
                }
            }
        }
        catch
        {
            // 启动早期只读语言选择失败时使用系统界面语言，不影响后续配置加载与错误处理。
        }

        LocaleCatalog.SetHostLocale(ResolveEarlyHostLocale(configuredLocale, CultureInfo.CurrentUICulture.Name));
    }

    private static bool IsTestHost()
    {
#if NEXUS_TEST_HOST
        return true;
#else
        return false;
#endif
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

}
