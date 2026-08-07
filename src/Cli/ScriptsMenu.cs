namespace NexusPipeline.Cli;

/// <summary>脚本实例管理子菜单：列表 / 新建 / 编辑 / 删除。</summary>
internal static class ScriptsMenu
{
    public static void Show(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            var lines = new List<string>();
            for (int i = 0; i < ctx.Scripts.Count; i++)
            {
                ScriptInstance script = ctx.Scripts[i];
                lines.Add($"{i + 1}. {script.Name} | 主程序：{script.MainExe} | 重试：{script.MaxAttempts} | 通知：{(script.NotifyEnabled ? "开" : "关")}");
            }
            string[] options =
            {
                "1. 新建脚本实例",
                "2. 编辑脚本实例（输入编号）",
                "3. 删除脚本实例（输入编号）",
                "0. 返回上级",
            };
            int width = Math.Max(lines.Count > 0 ? lines.Max(line => line.Length) : 10, options.Max(option => option.Length));
            width = Math.Max(width, "脚本实例管理".Length);
            var box = new List<string>
            {
                new string('=', width),
                "脚本实例管理",
                new string('=', width),
            };
            box.AddRange(lines);
            box.AddRange(options);
            box.Add(new string('=', width));
            Ui.Block(box);
            string? choice = Ui.Prompt("请选择：");
            if (choice is null)
            {
                return;
            }
            switch (choice.Trim())
            {
                case "0":
                    return;
                case "1":
                    EditScript(ctx, null);
                    break;
                case "2":
                case "3":
                {
                    string? number = Ui.Prompt($"请输入编号（1-{ctx.Scripts.Count}）：");
                    if (!int.TryParse(number?.Trim(), out int index) || index < 1 || index > ctx.Scripts.Count)
                    {
                        Console.WriteLine("[提示] 无效编号。");
                        break;
                    }
                    if (choice.Trim() == "2")
                    {
                        EditScript(ctx, ctx.Scripts[index - 1]);
                    }
                    else
                    {
                        string? answer = Ui.Prompt($"确定删除脚本实例「{ctx.Scripts[index - 1].Name}」吗？(Y/N)：");
                        if (Ui.IsYes(answer))
                        {
                            string removedName = ctx.Scripts[index - 1].Name;
                            ctx.Scripts.RemoveAt(index - 1);
                            DataStore.SaveScripts(ctx.Scripts);
                            Audit.Log(Audit.Manage, "删除脚本实例", removedName);
                            Console.WriteLine("[完成] 已删除。");
                        }
                        else
                        {
                            Console.WriteLine("[提示] 已取消。");
                        }
                    }
                    break;
                }
                default:
                    Console.WriteLine("[提示] 无效选项。");
                    break;
            }
            Console.WriteLine();
            Console.Write("按回车继续...");
            if (Console.ReadLine() is null)
            {
                return;
            }
        }
    }

    private static void EditScript(RuntimeContext ctx, ScriptInstance? current)
    {
        Ui.ClearScreen();
        ScriptInstance script = current?.Clone() ?? new ScriptInstance();
        Console.WriteLine(current is null ? "===== 新建脚本实例 =====" : $"===== 编辑脚本实例：{current.Name} =====");
        Console.WriteLine("（回车=保持当前值，Esc=放弃本项）");

        string? name = Ui.PromptText("脚本名称", script.Name);
        if (name is null)
        {
            return;
        }
        script.Name = name;
        script.RootPath = Ui.PromptText("脚本根目录", script.RootPath) ?? script.RootPath;
        script.MainExe = Ui.PromptText("脚本主程序路径", script.MainExe) ?? script.MainExe;
        script.Args = Ui.PromptText("脚本自启动参数", script.Args) ?? script.Args;
        script.ConfigPath = Ui.PromptText("配置文件路径/文件夹", script.ConfigPath) ?? script.ConfigPath;
        script.LogPath = Ui.PromptText("日志文件路径（或文件夹）", script.LogPath) ?? script.LogPath;

        string? launch = Ui.PromptText("运行前是否启动游戏（1=是 0=否）", script.LaunchGame ? "1" : "0");
        if (launch is not null)
        {
            script.LaunchGame = launch == "1";
        }
        if (script.LaunchGame)
        {
            script.GameExe = Ui.PromptText("游戏路径", script.GameExe) ?? script.GameExe;
            script.GameArgs = Ui.PromptText("游戏启动参数", script.GameArgs) ?? script.GameArgs;
            string? wait = Ui.PromptText("启动后等待秒数（默认 30）", script.GameWaitSeconds.ToString());
            if (wait is not null && int.TryParse(wait, out int waitSeconds) && waitSeconds >= 0)
            {
                script.GameWaitSeconds = waitSeconds;
            }
            string? force = Ui.PromptText("是否强制关闭游戏（1=是 0=否）", script.ForceCloseGame ? "1" : "0");
            if (force is not null)
            {
                script.ForceCloseGame = force == "1";
            }
        }

        string? attempts = Ui.PromptText("最大尝试次数（含首次，默认 3）", script.MaxAttempts.ToString());
        if (attempts is not null && int.TryParse(attempts, out int maxAttempts) && maxAttempts >= 1)
        {
            script.MaxAttempts = maxAttempts;
        }
        string? stall = Ui.PromptText("日志无更新超时（分钟，默认 5）", script.LogStallTimeoutMinutes.ToString());
        if (stall is not null && int.TryParse(stall, out int stallMinutes) && stallMinutes >= 1)
        {
            script.LogStallTimeoutMinutes = stallMinutes;
        }
        string? total = Ui.PromptText("运行总时间超时（分钟，默认 120）", script.TotalTimeoutMinutes.ToString());
        if (total is not null && int.TryParse(total, out int totalMinutes) && totalMinutes >= 1)
        {
            script.TotalTimeoutMinutes = totalMinutes;
        }
        string? markers = Ui.PromptText("自定义完成标志（逗号分隔，留空=内置关键词）", script.SuccessMarkers);
        if (markers is not null)
        {
            script.SuccessMarkers = markers;
        }
        string? notify = Ui.PromptText("是否发送运行状态通知（1=是 0=否）", script.NotifyEnabled ? "1" : "0");
        if (notify is not null)
        {
            script.NotifyEnabled = notify == "1";
        }

        string? limitError = current is null ? Limits.CheckScriptCount(ctx.Scripts.Count) : null;
        limitError ??= Limits.CheckNameBytes(script.Name, Limits.Current.MaxScriptNameBytes, "脚本名称");
        limitError ??= Limits.CheckAttempts(script.MaxAttempts);
        limitError ??= Limits.CheckStallMinutes(script.LogStallTimeoutMinutes);
        limitError ??= Limits.CheckTotalMinutes(script.TotalTimeoutMinutes);
        if (limitError is not null)
        {
            Console.WriteLine($"[错误] {limitError}，未保存。");
            return;
        }

        if (current is null)
        {
            ctx.Scripts.Add(script);
            Audit.Log(Audit.Manage, "添加脚本实例", script.Name);
        }
        else
        {
            ctx.Scripts[ctx.Scripts.IndexOf(current)] = script;
            Audit.Log(Audit.Manage, "修改脚本实例", script.Name);
        }
        DataStore.SaveScripts(ctx.Scripts);
        Console.WriteLine("[完成] 脚本实例已保存。");
    }
}
