using System.Reflection;

namespace NexusPipeline;

public class PluginManager
{
    private readonly List<IPlugin> _plugins = new();

    public IReadOnlyList<IPlugin> Plugins => _plugins;

    public void LoadAll()
    {
        foreach (IPlugin plugin in DiscoverBuiltIn())
        {
            _plugins.Add(plugin);
        }
        foreach (IPlugin plugin in DiscoverExternal())
        {
            _plugins.Add(plugin);
        }
        PruneUnknownPluginSettings();
        foreach (IPlugin plugin in _plugins)
        {
            bool enabled = RuntimeContext.Instance.Settings.EnabledPlugins.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase);
            if (enabled)
            {
                try
                {
                    plugin.Initialize(new PluginContext());
                    Logger.Log($"[插件] 已启用：{plugin.DisplayName} v{plugin.Version}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[插件] 插件「{plugin.DisplayName}」初始化失败：{ex.Message}");
                }
            }
            else
            {
                Logger.Log($"[插件] 已禁用：{plugin.DisplayName}");
            }
        }
    }

    private void PruneUnknownPluginSettings()
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        var known = new HashSet<string>(_plugins.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        int before = settings.EnabledPlugins.Count;
        settings.EnabledPlugins.RemoveAll(name => !known.Contains(name));
        if (settings.EnabledPlugins.Count != before)
        {
            ConfigStore.Save(settings);
            Logger.Log("[插件] 已清理设置中不存在的插件名。");
        }
    }

    public void ShutdownAll()
    {
        foreach (IPlugin plugin in _plugins)
        {
            try
            {
                plugin.Shutdown();
            }
            catch
            {
            }
        }
    }

    public bool IsEnabled(string name)
    {
        return RuntimeContext.Instance.Settings.EnabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public void SetEnabled(string name, bool enabled, string source = Audit.System)
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        bool exists = settings.EnabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (enabled && !exists)
        {
            settings.EnabledPlugins.Add(name);
        }
        else if (!enabled && exists)
        {
            settings.EnabledPlugins.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }
        ConfigStore.Save(settings);
        Audit.Log(source, $"{(enabled ? "启用" : "禁用")}插件", name);
        Logger.Log($"[插件] 已{(enabled ? "启用" : "禁用")}：{name}（重启后生效）。");
    }

    private static List<IPlugin> DiscoverBuiltIn()
    {
        var list = new List<IPlugin>
        {
            new NotifyPlugin(),
        };
        return list;
    }

    private static List<IPlugin> DiscoverExternal()
    {
        var list = new List<IPlugin>();
        if (!Directory.Exists(AppPaths.PluginsDir))
        {
            return list;
        }
        foreach (string dll in Directory.GetFiles(AppPaths.PluginsDir, "*.dll"))
        {
            try
            {
                Assembly assembly = Assembly.LoadFrom(dll);
                foreach (Type type in assembly.GetTypes())
                {
                    if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        if (Activator.CreateInstance(type) is IPlugin plugin)
                        {
                            list.Add(plugin);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[插件] 加载 {Path.GetFileName(dll)} 失败：{ex.Message}");
            }
        }
        return list;
    }
}
