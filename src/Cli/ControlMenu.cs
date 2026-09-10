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
                CliText.Get("menu.main_title", "NexusPipeline 枢链 管理菜单"),
                "==============================",
                CliText.Get("menu.main_scripts", "1. 脚本实例"),
                CliText.Get("menu.main_users", "2. 全局用户"),
                CliText.Get("menu.main_queues", "3. 调度队列"),
                CliText.Get("menu.main_runs", "4. 调度运行"),
                CliText.Get("menu.main_history", "5. 历史记录"),
                CliText.Get("menu.main_settings", "6. 设置与通知"),
                CliText.Get("menu.main_plugins", "7. 插件"),
                CliText.Get("menu.main_update", "8. 更新"),
                CliText.Get("menu.main_status", "9. 查看状态"),
                CliText.Get("menu.back", "0. 返回"),
                "==============================",
            });
            string? choice = Ui.Prompt(CliText.Get("prompt.choice", "请选择："));
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            switch (choice.Trim())
            {
                case "1":
                    ShowResource("script", CliText.Get("resource.script", "脚本实例"));
                    break;
                case "2":
                    ShowUsers();
                    break;
                case "3":
                    ShowResource("queue", CliText.Get("resource.queue", "调度队列"));
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
                    RunCommand("status");
                    PauseOrReturn();
                    break;
                default:
                    Console.WriteLine(CliText.Get("menu.invalid_choice", "[提示] 无效选项。"));
                    PauseOrReturn();
                    break;
            }
        }
    }

    public static void ShowResource(string resource, string label)
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand(resource, "list");
            Console.WriteLine();
            Console.WriteLine(CliText.Get("menu.resource_heading", "===== {label}操作 =====", ("label", label)));
            Console.WriteLine(CliText.Get("menu.action_create", "1. 新建"));
            Console.WriteLine(CliText.Get("menu.action_detail", "2. 查看详情"));
            Console.WriteLine(CliText.Get("menu.action_edit_json", "3. 编辑（JSON 文件）"));
            Console.WriteLine(CliText.Get("menu.action_delete", "4. 删除"));
            Console.WriteLine(CliText.Get("menu.action_reorder", "5. 调整顺序"));
            Console.WriteLine(CliText.Get("menu.back", "0. 返回"));
            string? choice = Ui.Prompt(CliText.Get("prompt.choice", "请选择："));
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
            Console.WriteLine(CliText.Get("menu.user_heading", "===== 全局用户操作 ====="));
            Console.WriteLine(CliText.Get("menu.action_create", "1. 新建"));
            Console.WriteLine(CliText.Get("menu.action_detail", "2. 查看详情"));
            Console.WriteLine(CliText.Get("menu.action_edit", "3. 编辑"));
            Console.WriteLine(CliText.Get("menu.action_delete", "4. 删除"));
            Console.WriteLine(CliText.Get("menu.action_reorder", "5. 调整顺序"));
            Console.WriteLine(CliText.Get("menu.plugin_user_binding", "6. 头像与绑定（命令行参数）"));
            Console.WriteLine(CliText.Get("menu.back", "0. 返回"));
            string? choice = Ui.Prompt(CliText.Get("prompt.choice", "请选择："));
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            bool shouldReturn = choice.Trim() switch
            {
                "1" => CreateUser(),
                "2" => GetResource("user", CliText.Resource("user")),
                "3" => UpdateUser(),
                "4" => DeleteUser(),
                "5" => ReorderResource("user", CliText.Resource("user")),
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
        string? file = Ui.Prompt(CliText.Get("prompt.json_file", "输入 JSON 文件路径（支持 - 从标准输入读取）："));
        if (!string.IsNullOrWhiteSpace(file))
        {
            RunCommand(resource, "create", "--file", file.Trim());
        }
        return false;
    }

    private static bool GetResource(string resource, string label)
    {
        string? target = Ui.Prompt(CliText.Get("prompt.target", "输入{label} ID 或名称：", ("label", label)));
        if (!string.IsNullOrWhiteSpace(target))
        {
            RunCommand(resource, "get", target.Trim());
        }
        return false;
    }

    private static bool UpdateResource(string resource, string label)
    {
        string? target = Ui.Prompt(CliText.Get("prompt.target", "输入{label} ID 或名称：", ("label", label)));
        string? file = Ui.Prompt(CliText.Get("prompt.json_file", "输入 JSON 文件路径（支持 - 从标准输入读取）："));
        if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(file))
        {
            RunCommand(resource, "update", target.Trim(), "--file", file.Trim());
        }
        return false;
    }

    private static bool DeleteResource(string resource, string label)
    {
        string? target = Ui.Prompt(CliText.Get("prompt.delete_target", "输入要删除的{label} ID 或名称：", ("label", label)));
        if (!string.IsNullOrWhiteSpace(target)
            && Ui.IsYes(Ui.Prompt(CliText.Get(
                "prompt.confirm_delete",
                "确认删除{label}「{target}」？(Y/N)：",
                ("label", label),
                ("target", target.Trim())))))
        {
            RunCommand(resource, "delete", target.Trim());
        }
        return false;
    }

    private static bool ReorderResource(string resource, string label)
    {
        string? ids = Ui.Prompt(CliText.Get("prompt.ids", "输入{label}完整 ID 列表（逗号分隔）：", ("label", label)));
        if (!string.IsNullOrWhiteSpace(ids))
        {
            RunCommand(resource, "reorder", "--ids", ids.Trim());
        }
        return false;
    }

    private static bool CreateUser()
    {
        string? name = Ui.Prompt(CliText.Get("prompt.username", "输入用户名："));
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        string? remark = Ui.Prompt(CliText.Get("prompt.remark", "输入备注（可为空）："));
        RunCommand("user", "create", "--name", name.Trim(), "--remark", remark ?? "");
        return false;
    }

    private static bool UpdateUser()
    {
        string? target = Ui.Prompt(CliText.Get("prompt.user_target", "输入用户 ID 或名称："));
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }
        string? name = Ui.Prompt(CliText.Get("prompt.new_username", "输入新用户名："));
        string? remark = Ui.Prompt(CliText.Get("prompt.new_remark", "输入新备注（可为空）："));
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine(CliText.Get("error.username_required", "[提示] 用户名不能为空。"));
            return false;
        }
        RunCommand("user", "update", target.Trim(), "--name", name.Trim(), "--remark", remark ?? "");
        return false;
    }

    private static bool DeleteUser()
    {
        string? target = Ui.Prompt(CliText.Get("prompt.delete_target", "输入要删除的{label} ID 或名称：", ("label", CliText.Get("resource.user", "用户"))));
        string? confirm = Ui.Prompt(CliText.Get("prompt.confirm_username", "完整输入用户名以确认删除："));
        if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(confirm))
        {
            RunCommand("user", "delete", target.Trim(), "--confirm", confirm.Trim());
        }
        return false;
    }

    private static bool ShowUserAdvancedHelp()
    {
        Console.WriteLine(CliText.Get("help.user_advanced", "头像与绑定支持以下正式 CLI 命令：\n  user avatar set <用户> --file <图片>\n  user avatar remove <用户>\n  user binding list <用户>\n  user binding add <用户> --script <脚本> --file <JSON>\n  user binding update <用户> <脚本> --file <JSON>\n  user binding delete <用户> <脚本>\n  user binding config start|done|cancel <用户> <脚本>"));
        return false;
    }

    private static void ShowRuns()
    {
        while (true)
        {
            Ui.ClearScreen();
            RunCommand("run", "list");
            Console.WriteLine();
            Console.WriteLine(CliText.Get("menu.run_heading", "===== 调度运行操作 ====="));
            Console.WriteLine(CliText.Get("menu.run_script", "1. 执行脚本实例"));
            Console.WriteLine(CliText.Get("menu.run_queue", "2. 执行调度队列"));
            Console.WriteLine(CliText.Get("menu.run_detail", "3. 查看运行详情"));
            Console.WriteLine(CliText.Get("menu.run_cancel", "4. 取消运行"));
            Console.WriteLine(CliText.Get("menu.back", "0. 返回"));
            string? choice = Ui.Prompt(CliText.Get("prompt.choice", "请选择："));
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
                    string? id = Ui.Prompt(CliText.Get("prompt.run_id", "输入运行 ID："));
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        RunCommand("run", "get", id.Trim());
                    }
                    break;
                }
                case "4":
                {
                    string? id = Ui.Prompt(CliText.Get("prompt.run_id", "输入运行 ID："));
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        RunCommand("run", "cancel", id.Trim());
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
        string label = kind == "script"
            ? CliText.Get("resource.script", "脚本实例")
            : CliText.Get("resource.queue", "调度队列");
        string? target = Ui.Prompt(CliText.Get("prompt.target", "输入{label} ID 或名称：", ("label", label)));
        if (!string.IsNullOrWhiteSpace(target))
        {
            RunCommand("run", kind, target.Trim(), "--detach");
        }
    }

    private static void ShowHistory()
    {
        Ui.ClearScreen();
        RunCommand("history", "list");
        string? id = Ui.Prompt(CliText.Get("prompt.history_id", "输入历史记录 ID 查看详情，回车返回："));
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
            Console.WriteLine(CliText.Get("menu.settings_heading", "===== 设置与通知 ====="));
            Console.WriteLine(CliText.Get("menu.settings_update", "1. 使用 JSON 文件更新设置"));
            Console.WriteLine(CliText.Get("menu.settings_test", "2. 发送测试通知"));
            Console.WriteLine(CliText.Get("menu.settings_restart", "3. 请求服务重启"));
            Console.WriteLine(CliText.Get("menu.back", "0. 返回"));
            string? choice = Ui.Prompt(CliText.Get("prompt.choice", "请选择："));
            if (choice is null || choice.Trim() == "0")
            {
                return;
            }
            switch (choice.Trim())
            {
                case "1":
                {
                    string? file = Ui.Prompt(CliText.Get("prompt.json_file_simple", "输入 JSON 文件路径："));
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
            Console.WriteLine(CliText.Get("menu.plugin_instruction", "输入插件名称与操作（enable/disable），回车返回："));
            string? name = Ui.Prompt(CliText.Get("prompt.plugin_name", "插件名："));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            string? action = Ui.Prompt(CliText.Get("prompt.action", "操作："));
            if (action is "enable" or "disable")
            {
                RunCommand("plugin", action, name.Trim());
            }
            else
            {
                Console.WriteLine(CliText.Get("error.plugin_action", "[提示] 操作必须为 enable 或 disable。"));
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
            Console.WriteLine(CliText.Get("menu.update_check", "1. 检查更新"));
            Console.WriteLine(CliText.Get("menu.update_download", "2. 下载更新"));
            Console.WriteLine(CliText.Get("menu.update_apply", "3. 应用更新"));
            Console.WriteLine(CliText.Get("menu.update_cancel", "4. 取消下载"));
            Console.WriteLine(CliText.Get("menu.back", "0. 返回"));
            string? choice = Ui.Prompt(CliText.Get("prompt.choice", "请选择："));
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

    private static void RunCommand(params string[] args)
    {
        CliCommandRouter.Run(args);
    }

    private static bool InvalidChoice()
    {
        Console.WriteLine(CliText.Get("menu.invalid_choice", "[提示] 无效选项。"));
        return false;
    }

    private static bool PauseOrReturn()
    {
        Console.WriteLine();
        Console.Write(CliText.Get("prompt.continue", "按回车继续..."));
        return Console.ReadLine() is not null;
    }
}
