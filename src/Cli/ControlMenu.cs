namespace NexusPipeline.Cli;

/// <summary>交互式管理菜单适配层：所有查询与变更均经本机 Control API 完成。</summary>
internal static class ControlMenu
{
    public static void Show()
    {
        while (true)
        {
            Ui.ClearScreen();
            Ui.Block(new List<string>
            {
                "==============================",
                "NexusPipeline 枢链 管理菜单",
                "==============================",
                "1. 脚本实例",
                "2. 全局用户",
                "3. 调度队列",
                "4. 调度运行",
                "5. 历史记录",
                "6. 设置与通知",
                "7. 插件",
                "8. 更新",
                "9. 维护",
                "10. 查看状态",
                "0. 返回",
                "==============================",
            });
            string? choice = Ui.Prompt("请选择：");
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            switch (choice.Trim())
            {
                case "1":
                    ShowResource("script", "脚本实例");
                    break;
                case "2":
                    ShowUsers();
                    break;
                case "3":
                    ShowResource("queue", "调度队列");
                    break;
                case "4":
                    ShowRuns();
                    break;
                case "5":
                    ShowHistory();
                    break;
                case "6":
                    ShowSettings();
                    break;
                case "7":
                    ShowPlugins();
                    break;
                case "8":
                    ShowUpdate();
                    break;
                case "9":
                    ShowMaintenance();
                    break;
                case "10":
                    RunCommand("status");
                    PauseOrReturn();
                    break;
                default:
                    Console.WriteLine("[提示] 无效选项。");
                    PauseOrReturn();
                    break;
            }
        }
    }

    public static void ShowSettingsForCompatibility() => ShowSettings();

    public static void ShowRunsForCompatibility() => ShowRuns();

    public static void ShowHistoryForCompatibility() => ShowHistory();

    public static void ShowPluginsForCompatibility() => ShowPlugins();

    public static void ShowMaintenanceForCompatibility() => ShowMaintenance();

    public static void ShowUpdateForCompatibility() => ShowUpdate();

    public static void ShowResource(string resource, string label)
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand(resource, "list");
            Console.WriteLine();
            Console.WriteLine($"===== {label}操作 =====");
            Console.WriteLine("1. 新建");
            Console.WriteLine("2. 查看详情");
            Console.WriteLine("3. 编辑（JSON 文件）");
            Console.WriteLine("4. 删除");
            Console.WriteLine("5. 调整顺序");
            Console.WriteLine("0. 返回");
            string? choice = Ui.Prompt("请选择：");
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            bool shouldReturn = choice.Trim() switch
            {
                "1" => CreateResource(resource),
                "2" => GetResource(resource, label),
                "3" => UpdateResource(resource, label),
                "4" => DeleteResource(resource, label),
                "5" => ReorderResource(resource, label),
                _ => InvalidChoice(),
            };
            if (shouldReturn || !PauseOrReturn())
            {
                return;
            }
        }
    }

    private static void ShowUsers()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("user", "list");
            Console.WriteLine();
            Console.WriteLine("===== 全局用户操作 =====");
            Console.WriteLine("1. 新建");
            Console.WriteLine("2. 查看详情");
            Console.WriteLine("3. 编辑");
            Console.WriteLine("4. 删除");
            Console.WriteLine("5. 调整顺序");
            Console.WriteLine("6. 头像与绑定（命令行参数）");
            Console.WriteLine("0. 返回");
            string? choice = Ui.Prompt("请选择：");
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            bool shouldReturn = choice.Trim() switch
            {
                "1" => CreateUser(),
                "2" => GetResource("user", "用户"),
                "3" => UpdateUser(),
                "4" => DeleteUser(),
                "5" => ReorderResource("user", "用户"),
                "6" => ShowUserAdvancedHelp(),
                _ => InvalidChoice(),
            };
            if (shouldReturn || !PauseOrReturn())
            {
                return;
            }
        }
    }

    private static bool CreateResource(string resource)
    {
        string? file = Ui.Prompt("输入 JSON 文件路径（支持 - 从标准输入读取）：");
        if (!string.IsNullOrWhiteSpace(file))
        {
            RunCommand(resource, "create", "--file", file.Trim());
        }
        return false;
    }

    private static bool GetResource(string resource, string label)
    {
        string? target = Ui.Prompt($"输入{label} ID 或名称：");
        if (!string.IsNullOrWhiteSpace(target))
        {
            RunCommand(resource, "get", target.Trim());
        }
        return false;
    }

    private static bool UpdateResource(string resource, string label)
    {
        string? target = Ui.Prompt($"输入{label} ID 或名称：");
        string? file = Ui.Prompt("输入 JSON 文件路径（支持 - 从标准输入读取）：");
        if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(file))
        {
            RunCommand(resource, "update", target.Trim(), "--file", file.Trim());
        }
        return false;
    }

    private static bool DeleteResource(string resource, string label)
    {
        string? target = Ui.Prompt($"输入要删除的{label} ID 或名称：");
        if (!string.IsNullOrWhiteSpace(target)
            && Ui.IsYes(Ui.Prompt($"确认删除{label}「{target.Trim()}」？(Y/N)：")))
        {
            RunCommand(resource, "delete", target.Trim());
        }
        return false;
    }

    private static bool ReorderResource(string resource, string label)
    {
        string? ids = Ui.Prompt($"输入{label}完整 ID 列表（逗号分隔）：");
        if (!string.IsNullOrWhiteSpace(ids))
        {
            RunCommand(resource, "reorder", "--ids", ids.Trim());
        }
        return false;
    }

    private static bool CreateUser()
    {
        string? name = Ui.Prompt("输入用户名：");
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        string? remark = Ui.Prompt("输入备注（可为空）：");
        RunCommand("user", "create", "--name", name.Trim(), "--remark", remark ?? "");
        return false;
    }

    private static bool UpdateUser()
    {
        string? target = Ui.Prompt("输入用户 ID 或名称：");
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }
        string? name = Ui.Prompt("输入新用户名：");
        string? remark = Ui.Prompt("输入新备注（可为空）：");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("[提示] 用户名不能为空。");
            return false;
        }
        RunCommand("user", "update", target.Trim(), "--name", name.Trim(), "--remark", remark ?? "");
        return false;
    }

    private static bool DeleteUser()
    {
        string? target = Ui.Prompt("输入要删除的用户 ID 或名称：");
        string? confirm = Ui.Prompt("完整输入用户名以确认删除：");
        if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(confirm))
        {
            RunCommand("user", "delete", target.Trim(), "--confirm", confirm.Trim());
        }
        return false;
    }

    private static bool ShowUserAdvancedHelp()
    {
        Console.WriteLine("头像与绑定支持以下正式 CLI 命令：");
        Console.WriteLine("  user avatar set <用户> --file <图片>");
        Console.WriteLine("  user avatar remove <用户>");
        Console.WriteLine("  user binding list <用户>");
        Console.WriteLine("  user binding add <用户> --script <脚本> --file <JSON>");
        Console.WriteLine("  user binding update <用户> <脚本> --file <JSON>");
        Console.WriteLine("  user binding delete <用户> <脚本>");
        Console.WriteLine("  user binding config start|done|cancel <用户> <脚本>");
        return false;
    }

    private static void ShowRuns()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("run", "list");
            Console.WriteLine();
            Console.WriteLine("===== 调度运行操作 =====");
            Console.WriteLine("1. 执行脚本实例");
            Console.WriteLine("2. 执行调度队列");
            Console.WriteLine("3. 查看运行详情");
            Console.WriteLine("4. 取消运行");
            Console.WriteLine("0. 返回");
            string? choice = Ui.Prompt("请选择：");
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            switch (choice.Trim())
            {
                case "1":
                    RunTarget("script");
                    break;
                case "2":
                    RunTarget("queue");
                    break;
                case "3":
                {
                    string? id = Ui.Prompt("输入运行 ID：");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        RunCommand("run", "get", id.Trim());
                    }
                    break;
                }
                case "4":
                {
                    string? id = Ui.Prompt("输入运行 ID：");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        RunCommand("cancel", id.Trim());
                    }
                    break;
                }
                default:
                    InvalidChoice();
                    break;
            }
            if (!PauseOrReturn())
            {
                return;
            }
        }
    }

    private static void RunTarget(string kind)
    {
        string label = kind == "script" ? "脚本实例" : "调度队列";
        string? target = Ui.Prompt($"输入{label} ID 或名称：");
        if (!string.IsNullOrWhiteSpace(target))
        {
            RunCommand("run", kind, target.Trim(), "--detach");
        }
    }

    private static void ShowHistory()
    {
        Ui.ClearScreen();
        RunCommand("history", "list");
        string? id = Ui.Prompt("输入历史记录 ID 查看详情，回车返回：");
        if (!string.IsNullOrWhiteSpace(id))
        {
            RunCommand("history", "get", id.Trim());
            PauseOrReturn();
        }
    }

    private static void ShowSettings()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("settings", "get");
            Console.WriteLine();
            Console.WriteLine("===== 设置与通知 =====");
            Console.WriteLine("1. 使用 JSON 文件更新设置");
            Console.WriteLine("2. 发送测试通知");
            Console.WriteLine("3. 请求服务重启");
            Console.WriteLine("0. 返回");
            string? choice = Ui.Prompt("请选择：");
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            switch (choice.Trim())
            {
                case "1":
                {
                    string? file = Ui.Prompt("输入 JSON 文件路径：");
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        RunCommand("settings", "update", "--file", file.Trim());
                    }
                    break;
                }
                case "2":
                    RunCommand("settings", "test");
                    break;
                case "3":
                    RunCommand("settings", "restart");
                    break;
                default:
                    InvalidChoice();
                    break;
            }
            if (!PauseOrReturn())
            {
                return;
            }
        }
    }

    private static void ShowPlugins()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("plugin", "list");
            Console.WriteLine();
            Console.WriteLine("输入插件名称与操作（enable/disable），回车返回：");
            string? name = Ui.Prompt("插件名：");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            string? action = Ui.Prompt("操作：");
            if (action is "enable" or "disable")
            {
                RunCommand("plugin", action, name.Trim());
            }
            else
            {
                Console.WriteLine("[提示] 操作必须为 enable 或 disable。");
            }
            if (!PauseOrReturn())
            {
                return;
            }
        }
    }

    private static void ShowUpdate()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("update", "status");
            Console.WriteLine();
            Console.WriteLine("1. 检查更新");
            Console.WriteLine("2. 下载更新");
            Console.WriteLine("3. 应用更新");
            Console.WriteLine("4. 取消下载");
            Console.WriteLine("0. 返回");
            string? choice = Ui.Prompt("请选择：");
            string? sub = choice?.Trim() switch
            {
                "1" => "check",
                "2" => "download",
                "3" => "apply",
                "4" => "cancel",
                "0" or null => null,
                _ => "",
            };
            if (sub is null)
            {
                return;
            }
            if (sub.Length == 0)
            {
                InvalidChoice();
            }
            else
            {
                RunCommand("update", sub);
            }
            if (!PauseOrReturn())
            {
                return;
            }
        }
    }

    private static void ShowMaintenance()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("maintenance", "list");
            Console.WriteLine();
            Console.WriteLine("1. 清理遗留用户目录");
            Console.WriteLine("0. 返回");
            string? choice = Ui.Prompt("请选择：");
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            if (choice.Trim() == "1")
            {
                string? scriptId = Ui.Prompt("脚本 ID：");
                string? userKey = Ui.Prompt("遗留用户键：");
                if (!string.IsNullOrWhiteSpace(scriptId) && !string.IsNullOrWhiteSpace(userKey))
                {
                    RunCommand("maintenance", "prune", "--script-id", scriptId.Trim(), "--user-key", userKey.Trim());
                }
            }
            else
            {
                InvalidChoice();
            }
            if (!PauseOrReturn())
            {
                return;
            }
        }
    }

    private static void RunCommand(params string[] args)
    {
        CliCommandRouter.Run(args);
    }

    private static bool InvalidChoice()
    {
        Console.WriteLine("[提示] 无效选项。");
        return false;
    }

    private static bool PauseOrReturn()
    {
        Console.WriteLine();
        Console.Write("按回车继续...");
        return Console.ReadLine() is not null;
    }
}
