using System.Globalization;

namespace NexusPipeline;

public static class ManageMenu
{
    public static void Show()
    {
        while (true)
        {
            Ui.ClearScreen();
            RuntimeContext ctx = RuntimeContext.Instance;
            string[] options =
            {
                $"1. 脚本实例管理（当前：{ctx.Scripts.Count} 个）",
                $"2. 调度队列管理（当前：{ctx.Queues.Count} 个）",
                "3. 调度中心（手动执行脚本或队列）",
                "4. 历史记录",
                "5. 插件",
                "6. 设置",
                "7. 查看状态",
                "0. 退出（关闭窗口）",
            };
            string title = "NexusPipeline 枢链 管理菜单";
            int width = Math.Max(title.Length, options.Max(option => option.Length));
            var lines = new List<string>
            {
                new string('=', width),
                title,
                new string('=', width),
            };
            lines.AddRange(options);
            lines.Add(new string('=', width));
            Ui.Block(lines);
            string? choice = Ui.Prompt("请选择：");
            if (choice is null)
            {
                return;
            }
            bool skipPause = false;
            switch (choice.Trim())
            {
                case "0":
                    return;
                case "1":
                    ScriptsMenu(ctx);
                    skipPause = true;
                    break;
                case "2":
                    QueuesMenu(ctx);
                    skipPause = true;
                    break;
                case "3":
                    DispatchMenu(ctx);
                    break;
                case "4":
                    HistoryMenu(ctx);
                    break;
                case "5":
                    PluginsMenu(ctx);
                    break;
                case "6":
                    SettingsMenu(ctx);
                    break;
                case "7":
                    ShowStatus();
                    break;
                default:
                    Console.WriteLine("[提示] 无效选项。");
                    break;
            }
            if (!skipPause)
            {
                Console.WriteLine();
                Console.Write("按回车继续...");
                if (Console.ReadLine() is null)
                {
                    return;
                }
            }
        }
    }

    private static void ScriptsMenu(RuntimeContext ctx)
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
                            ctx.SaveScripts();
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

        string? name = PromptText("脚本名称", script.Name);
        if (name is null)
        {
            return;
        }
        script.Name = name;
        script.RootPath = PromptText("脚本根目录", script.RootPath) ?? script.RootPath;
        script.MainExe = PromptText("脚本主程序路径", script.MainExe) ?? script.MainExe;
        script.Args = PromptText("脚本自启动参数", script.Args) ?? script.Args;
        script.ConfigPath = PromptText("配置文件路径/文件夹", script.ConfigPath) ?? script.ConfigPath;
        script.LogPath = PromptText("日志文件路径（或文件夹）", script.LogPath) ?? script.LogPath;

        string? launch = PromptText("运行前是否启动游戏（1=是 0=否）", script.LaunchGame ? "1" : "0");
        if (launch is not null)
        {
            script.LaunchGame = launch == "1";
        }
        if (script.LaunchGame)
        {
            script.GameExe = PromptText("游戏路径", script.GameExe) ?? script.GameExe;
            script.GameArgs = PromptText("游戏启动参数", script.GameArgs) ?? script.GameArgs;
            string? wait = PromptText("启动后等待秒数（默认 30）", script.GameWaitSeconds.ToString());
            if (wait is not null && int.TryParse(wait, out int waitSeconds) && waitSeconds >= 0)
            {
                script.GameWaitSeconds = waitSeconds;
            }
            string? force = PromptText("是否强制关闭游戏（1=是 0=否）", script.ForceCloseGame ? "1" : "0");
            if (force is not null)
            {
                script.ForceCloseGame = force == "1";
            }
        }

        string? attempts = PromptText("最大尝试次数（含首次，默认 3）", script.MaxAttempts.ToString());
        if (attempts is not null && int.TryParse(attempts, out int maxAttempts) && maxAttempts >= 1)
        {
            script.MaxAttempts = maxAttempts;
        }
        string? stall = PromptText("日志无更新超时（分钟，默认 5）", script.LogStallTimeoutMinutes.ToString());
        if (stall is not null && int.TryParse(stall, out int stallMinutes) && stallMinutes >= 1)
        {
            script.LogStallTimeoutMinutes = stallMinutes;
        }
        string? total = PromptText("运行总时间超时（分钟，默认 120）", script.TotalTimeoutMinutes.ToString());
        if (total is not null && int.TryParse(total, out int totalMinutes) && totalMinutes >= 1)
        {
            script.TotalTimeoutMinutes = totalMinutes;
        }
        string? markers = PromptText("自定义完成标志（逗号分隔，留空=内置关键词）", script.SuccessMarkers);
        if (markers is not null)
        {
            script.SuccessMarkers = markers;
        }
        string? notify = PromptText("是否发送运行状态通知（1=是 0=否）", script.NotifyEnabled ? "1" : "0");
        if (notify is not null)
        {
            script.NotifyEnabled = notify == "1";
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
        ctx.SaveScripts();
        Console.WriteLine("[完成] 脚本实例已保存。");
    }

    private static void QueuesMenu(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            var lines = new List<string>();
            for (int i = 0; i < ctx.Queues.Count; i++)
            {
                DispatchQueue queue = ctx.Queues[i];
                string tasks = string.Join(" → ", queue.Tasks.OrderBy(task => task.Index).Select(task =>
                {
                    ScriptInstance? script = ctx.FindScript(task.ScriptInstanceId);
                    return script?.Name ?? "(缺失)";
                }));
                lines.Add($"{i + 1}. {queue.Name} | {QueueRule.AutoRunModeDesc(queue.AutoRunMode)} | 完成操作：{QueueRule.CompletionActionDesc(queue.CompletionAction)} | 任务：{(tasks.Length > 0 ? tasks : "无")}");
            }
            string[] options =
            {
                "1. 新建调度队列",
                "2. 编辑调度队列（输入编号）",
                "3. 删除调度队列（输入编号）",
                "0. 返回上级",
            };
            int width = Math.Max(lines.Count > 0 ? lines.Max(line => line.Length) : 10, options.Max(option => option.Length));
            width = Math.Max(width, "调度队列管理".Length);
            var box = new List<string>
            {
                new string('=', width),
                "调度队列管理",
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
                    EditQueue(ctx, null);
                    break;
                case "2":
                case "3":
                {
                    string? number = Ui.Prompt($"请输入编号（1-{ctx.Queues.Count}）：");
                    if (!int.TryParse(number?.Trim(), out int index) || index < 1 || index > ctx.Queues.Count)
                    {
                        Console.WriteLine("[提示] 无效编号。");
                        break;
                    }
                    if (choice.Trim() == "2")
                    {
                        EditQueue(ctx, ctx.Queues[index - 1]);
                    }
                    else
                    {
                        string? answer = Ui.Prompt($"确定删除调度队列「{ctx.Queues[index - 1].Name}」吗？(Y/N)：");
                        if (Ui.IsYes(answer))
                        {
                            string removedName = ctx.Queues[index - 1].Name;
                            ctx.Queues.RemoveAt(index - 1);
                            ctx.SaveQueues();
                            Audit.Log(Audit.Manage, "删除调度队列", removedName);
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

    private static void EditQueue(RuntimeContext ctx, DispatchQueue? current)
    {
        Ui.ClearScreen();
        DispatchQueue queue = current?.Clone() ?? new DispatchQueue();
        Console.WriteLine(current is null ? "===== 新建调度队列 =====" : $"===== 编辑调度队列：{current.Name} =====");

        string? name = PromptText("队列名称", queue.Name);
        if (name is null)
        {
            return;
        }
        queue.Name = name;

        Console.WriteLine("自动运行方式：1=启动时运行 2=定时运行");
        string? mode = PromptText("选择", queue.AutoRunMode == "startup" ? "1" : "2");
        if (mode is not null)
        {
            queue.AutoRunMode = mode switch { "1" => "startup", "2" => "scheduled", _ => queue.AutoRunMode };
        }

        Console.WriteLine("完成操作：1=无操作 2=退出软件 3=休眠 4=重启 5=关机");
        string? action = PromptText("选择", queue.CompletionAction switch
        {
            "exit" => "2",
            "sleep" => "3",
            "reboot" => "4",
            "shutdown" => "5",
            _ => "1",
        });
        if (action is not null)
        {
            queue.CompletionAction = action switch
            {
                "2" => "exit",
                "3" => "sleep",
                "4" => "reboot",
                "5" => "shutdown",
                "1" => "none",
                _ => queue.CompletionAction,
            };
        }

        string? notify = PromptText("队列级通知（开启后统一发送所有脚本状态，覆盖实例级设置；1=是 0=否）", queue.NotifyEnabled ? "1" : "0");
        if (notify is not null)
        {
            queue.NotifyEnabled = notify == "1";
        }

        if (queue.AutoRunMode == "scheduled")
        {
            EditTimeSets(queue);
        }

        EditTasks(ctx, queue);

        if (current is null)
        {
            ctx.Queues.Add(queue);
            Audit.Log(Audit.Manage, "添加调度队列", $"{queue.Name}（任务 {queue.Tasks.Count} 项）");
        }
        else
        {
            ctx.Queues[ctx.Queues.IndexOf(current)] = queue;
            Audit.Log(Audit.Manage, "修改调度队列", $"{queue.Name}（任务 {queue.Tasks.Count} 项）");
        }
        ctx.SaveQueues();
        Console.WriteLine("[完成] 调度队列已保存。");
    }

    private static void EditTimeSets(DispatchQueue queue)
    {
        Console.WriteLine();
        Console.WriteLine("===== 定时列表（当前 {0} 条） =====", queue.TimeSets.Count);
        Console.WriteLine("1. 添加定时  2. 删除定时（输入序号）  回车=跳过");
        for (int i = 0; i < queue.TimeSets.Count; i++)
        {
            QueueTimeSet ts = queue.TimeSets[i];
            Console.WriteLine($"  {i + 1}. [{DayDesc(ts.Days)}] {ts.Time} {(ts.Enabled ? "启用" : "停用")}");
        }
        string? choice = Ui.Prompt("选择：");
        if (string.IsNullOrWhiteSpace(choice) || choice.Trim() == "0")
        {
            return;
        }
        if (choice.Trim() == "1")
        {
            var ts = new QueueTimeSet();
            string? days = Ui.Prompt("执行周期（周一~周日输入 1-7，多选逗号分隔，如 1,3,5）：");
            if (string.IsNullOrWhiteSpace(days))
            {
                return;
            }
            foreach (string part in days.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int day) && day is >= 1 and <= 7)
                {
                    ts.Days.Add(day % 7);
                }
            }
            if (ts.Days.Count == 0)
            {
                Console.WriteLine("[提示] 未选择有效星期，已取消。");
                return;
            }
            string? time = Ui.Prompt("执行时间（hh:mm，如 05:30）：");
            if (time is null || !TimeOnly.TryParseExact(time.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                Console.WriteLine("[提示] 时间格式无效，已取消。");
                return;
            }
            ts.Time = time.Trim();
            string? enabled = Ui.Prompt("是否启用（1=是 0=否，默认启用）：");
            if (enabled is not null && enabled.Trim() == "0")
            {
                ts.Enabled = false;
            }
            queue.TimeSets.Add(ts);
            Console.WriteLine("[完成] 已添加定时。");
        }
        else if (choice.Trim() == "2")
        {
            string? number = Ui.Prompt($"输入要删除的定时序号（1-{queue.TimeSets.Count}）：");
            if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= queue.TimeSets.Count)
            {
                queue.TimeSets.RemoveAt(index - 1);
                Console.WriteLine("[完成] 已删除。");
            }
            else
            {
                Console.WriteLine("[提示] 无效序号。");
            }
        }
    }

    private static void EditTasks(RuntimeContext ctx, DispatchQueue queue)
    {
        Console.WriteLine();
        Console.WriteLine("===== 任务列表（按序号先后执行） =====");
        bool keepEditing = true;
        while (keepEditing)
        {
            var ordered = queue.Tasks.OrderBy(task => task.Index).ToList();
            Console.WriteLine("当前任务：");
            if (ordered.Count == 0)
            {
                Console.WriteLine("  （无）");
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                ScriptInstance? script = ctx.FindScript(ordered[i].ScriptInstanceId);
                Console.WriteLine($"  {i + 1}. {script?.Name ?? "(脚本不存在)"}");
            }
            Console.WriteLine("1. 添加任务  2. 删除任务（输入序号）  3. 调整顺序（上移/下移）  回车=完成");
            string? choice = Ui.Prompt("选择：");
            if (string.IsNullOrWhiteSpace(choice) || choice.Trim() == "0")
            {
                keepEditing = false;
                break;
            }
            if (choice.Trim() == "1")
            {
                if (ctx.Scripts.Count == 0)
                {
                    Console.WriteLine("[提示] 请先创建脚本实例。");
                    continue;
                }
                Console.WriteLine("可选脚本：");
                for (int i = 0; i < ctx.Scripts.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {ctx.Scripts[i].Name}");
                }
                string? number = Ui.Prompt("输入脚本编号：");
                if (int.TryParse(number?.Trim(), out int scriptIndex) && scriptIndex >= 1 && scriptIndex <= ctx.Scripts.Count)
                {
                    queue.Tasks.Add(new QueueTask
                    {
                        Index = queue.Tasks.Count,
                        ScriptInstanceId = ctx.Scripts[scriptIndex - 1].Id,
                    });
                    Console.WriteLine("[完成] 已添加任务。");
                }
                else
                {
                    Console.WriteLine("[提示] 无效编号。");
                }
            }
            else if (choice.Trim() == "2")
            {
                string? number = Ui.Prompt($"输入要删除的任务序号（1-{ordered.Count}）：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ordered.Count)
                {
                    queue.Tasks.Remove(ordered[index - 1]);
                    for (int i = 0; i < queue.Tasks.Count; i++)
                    {
                        queue.Tasks[i].Index = i;
                    }
                    Console.WriteLine("[完成] 已删除。");
                }
                else
                {
                    Console.WriteLine("[提示] 无效序号。");
                }
            }
            else if (choice.Trim() == "3")
            {
                string? number = Ui.Prompt($"输入要调整的任务序号（1-{ordered.Count}，上移=前移）：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ordered.Count)
                {
                    string? direction = Ui.Prompt("1=上移 2=下移：");
                    if (direction?.Trim() == "1" && index > 1)
                    {
                        Swap(ordered[index - 1], ordered[index - 2]);
                    }
                    else if (direction?.Trim() == "2" && index < ordered.Count)
                    {
                        Swap(ordered[index - 1], ordered[index]);
                    }
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        ordered[i].Index = i;
                    }
                    Console.WriteLine("[完成] 顺序已调整。");
                }
            }
        }
    }

    private static void Swap(QueueTask a, QueueTask b)
    {
        (a.ScriptInstanceId, b.ScriptInstanceId) = (b.ScriptInstanceId, a.ScriptInstanceId);
    }

    private static void DispatchMenu(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        var lines = new List<string>
        {
            "===== 调度中心 =====",
        };
        IReadOnlyList<RunningExecution> active = ctx.Center.Active;
        if (active.Count == 0)
        {
            lines.Add("当前没有正在运行的任务。");
        }
        else
        {
            foreach (RunningExecution exec in active)
            {
                lines.Add($"[{exec.Id[..8]}] {exec.TargetName}（{exec.Kind}，{(exec.Mode == "auto" ? "自动" : "手动")}）当前：{exec.CurrentScriptName} {exec.CurrentStatus} 第 {exec.CurrentAttempt}/{exec.CurrentMaxAttempts} 次");
            }
        }
        lines.Add("");
        lines.Add("1. 手动执行脚本实例");
        lines.Add("2. 手动执行调度队列");
        lines.Add("3. 取消运行（输入运行 ID）");
        lines.Add("0. 返回上级");
        Ui.Block(lines);
        string? choice = Ui.Prompt("请选择：");
        switch (choice?.Trim())
        {
            case "0":
            case null:
                return;
            case "1":
            {
                if (ctx.Scripts.Count == 0)
                {
                    Console.WriteLine("[提示] 没有脚本实例。");
                    break;
                }
                for (int i = 0; i < ctx.Scripts.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {ctx.Scripts[i].Name}");
                }
                string? number = Ui.Prompt("输入脚本编号：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ctx.Scripts.Count)
                {
                    try
                    {
                        ctx.Center.StartScript(ctx.Scripts[index - 1].Id, "manual", Audit.Manage);
                        Console.WriteLine("[完成] 已开始执行，进度见运行日志。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[错误] {ex.Message}");
                    }
                }
                break;
            }
            case "2":
            {
                if (ctx.Queues.Count == 0)
                {
                    Console.WriteLine("[提示] 没有调度队列。");
                    break;
                }
                for (int i = 0; i < ctx.Queues.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {ctx.Queues[i].Name}");
                }
                string? number = Ui.Prompt("输入队列编号：");
                if (int.TryParse(number?.Trim(), out int index) && index >= 1 && index <= ctx.Queues.Count)
                {
                    try
                    {
                        ctx.Center.StartQueue(ctx.Queues[index - 1].Id, "manual", Audit.Manage);
                        Console.WriteLine("[完成] 已开始执行，进度见运行日志。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[错误] {ex.Message}");
                    }
                }
                break;
            }
            case "3":
            {
                string? runId = Ui.Prompt("输入运行 ID（前 8 位即可）：");
                RunningExecution? exec = active.FirstOrDefault(item => item.Id.StartsWith(runId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
                if (exec is null)
                {
                    Console.WriteLine("[提示] 未找到该运行任务。");
                }
                else
                {
                    ctx.Center.Cancel(exec.Id, Audit.Manage);
                    Console.WriteLine("[完成] 已发送取消请求。");
                }
                break;
            }
            default:
                Console.WriteLine("[提示] 无效选项。");
                break;
        }
    }

    private static void HistoryMenu(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        List<RunRecord> records = ctx.History.Recent(ctx.Settings.HistoryRetentionDays);
        Console.WriteLine("===== 历史记录（最近 {0} 天，共 {1} 条） =====", ctx.Settings.HistoryRetentionDays, records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            RunRecord record = records[i];
            string status = record.Status switch
            {
                "success" => "成功",
                "cancelled" => "已取消",
                _ => "失败",
            };
            Console.WriteLine($"  {i + 1}. {record.StartTime:MM-dd HH:mm} {record.ScriptName} [{status}]（{record.ResultDetail}）{(string.IsNullOrEmpty(record.QueueName) ? "" : $" 队列：{record.QueueName}")}");
        }
        Console.WriteLine();
        Console.WriteLine("输入序号查看详情，回车返回：");
        string? choice = Ui.Prompt("");
        if (int.TryParse(choice?.Trim(), out int index) && index >= 1 && index <= records.Count)
        {
            ShowRecordDetail(records[index - 1]);
        }
    }

    private static void ShowRecordDetail(RunRecord record)
    {
        Ui.ClearScreen();
        Console.WriteLine($"===== {record.ScriptName} 运行详情 =====");
        Console.WriteLine($"模式：{(record.Mode == "auto" ? "自动运行" : "手动运行")} | 队列：{(string.IsNullOrEmpty(record.QueueName) ? "-" : record.QueueName)}");
        Console.WriteLine($"开始：{record.StartTime:yyyy-MM-dd HH:mm:ss} | 结束：{record.EndTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"最终状态：{record.Status} | {record.ResultDetail}");
        foreach (RunAttempt attempt in record.AttemptDetails)
        {
            Console.WriteLine();
            Console.WriteLine($"--- 第 {attempt.Number} 次尝试：{attempt.Status} ---");
            Console.WriteLine($"原因：{attempt.Reason}");
            Console.WriteLine($"时间：{attempt.StartTime:HH:mm:ss} - {attempt.EndTime:HH:mm:ss}");
            if (attempt.LogTail.Count > 0)
            {
                Console.WriteLine("日志尾部：");
                foreach (string line in attempt.LogTail.TakeLast(10))
                {
                    Console.WriteLine($"  {line}");
                }
            }
            if (!string.IsNullOrWhiteSpace(attempt.OutputTail))
            {
                Console.WriteLine("控制台输出尾部：");
                foreach (string line in attempt.OutputTail.Split('\n').TakeLast(10))
                {
                    Console.WriteLine($"  {line}");
                }
            }
        }
        Console.WriteLine();
        Console.Write("按回车返回...");
        Console.ReadLine();
    }

    private static void PluginsMenu(RuntimeContext ctx)
    {
        Ui.ClearScreen();
        Console.WriteLine("===== 插件 =====");
        foreach (IPlugin plugin in ctx.Plugins.Plugins)
        {
            bool enabled = ctx.Plugins.IsEnabled(plugin.Name);
            Console.WriteLine($"  {plugin.DisplayName} v{plugin.Version} [{(enabled ? "已启用" : "已禁用")}]");
            Console.WriteLine($"    {plugin.Description}");
        }
        Console.WriteLine();
        Console.WriteLine("1. 启用/禁用插件（输入插件名）");
        Console.WriteLine("0. 返回上级");
        string? choice = Ui.Prompt("请选择：");
        switch (choice?.Trim())
        {
            case "0":
            case null:
                return;
            case "1":
            {
                string? name = Ui.Prompt("输入插件名：");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
                bool enabled = ctx.Plugins.IsEnabled(name.Trim());
                ctx.Plugins.SetEnabled(name.Trim(), !enabled, Audit.Manage);
                Console.WriteLine($"[完成] {name.Trim()} 已{(enabled ? "禁用（下次启动生效）" : "启用（下次启动生效）")}。");
                break;
            }
            default:
                Console.WriteLine("[提示] 无效选项。");
                break;
        }
    }

    private static void SettingsMenu(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            AppSettings s = ctx.Settings;
            (bool webhookOk, string webhookReason) = WebhookSender.Status(s);
            (bool smtpOk, string smtpReason) = SmtpSender.Status(s);
            string[] options =
            {
                $"1. 开机自启动（当前：{(s.AutoStart ? "开" : "关")}）",
                $"2. 轻量运行模式（当前：{(s.LightweightMode ? "开" : "关")}，重启生效）",
                $"3. 历史保留天数（当前：{s.HistoryRetentionDays} 天）",
                $"4. Web 端口（当前：{s.WebPort}）",
                $"5. 启动后自动打开浏览器（当前：{(s.AutoOpenBrowser ? "开" : "关")}）",
                $"6. 通知渠道（Webhook：{webhookReason} | SMTP：{smtpReason} | 开关：Webhook {(s.WebhookEnabled ? "开" : "关")} / SMTP {(s.SmtpEnabled ? "开" : "关")}）",
                "7. 清理过期历史与日志",
                "0. 返回上级",
            };
            int width = options.Max(option => option.Length);
            var lines = new List<string>
            {
                new string('=', width),
                "设置",
                new string('=', width),
            };
            lines.AddRange(options);
            lines.Add(new string('=', width));
            Ui.Block(lines);
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
                    s.AutoStart = !s.AutoStart;
                    ConfigStore.Save(s);
                    TaskRegistration.SyncWithSettings(s);
                    Audit.Log(Audit.Manage, "修改设置", $"开机自启动→{(s.AutoStart ? "开" : "关")}");
                    Console.WriteLine($"[完成] 开机自启动已{(s.AutoStart ? "开启" : "关闭")}。");
                    break;
                case "2":
                    s.LightweightMode = !s.LightweightMode;
                    ConfigStore.Save(s);
                    Audit.Log(Audit.Manage, "修改设置", $"轻量运行模式→{(s.LightweightMode ? "开" : "关")}");
                    Console.WriteLine($"[完成] 轻量运行模式已{(s.LightweightMode ? "开启" : "关闭")}（重启生效）。");
                    break;
                case "3":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"保留天数（当前：{s.HistoryRetentionDays}，回车=不变）：");
                    if (result == EditResult.Entered && int.TryParse(value.Trim(), out int days) && days >= 1)
                    {
                        s.HistoryRetentionDays = days;
                        ConfigStore.Save(s);
                        Audit.Log(Audit.Manage, "修改设置", $"历史保留天数→{days}");
                        Console.WriteLine("[完成] 已保存。");
                    }
                    break;
                }
                case "4":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"Web 端口（当前：{s.WebPort}，回车=不变）：");
                    if (result == EditResult.Entered && int.TryParse(value.Trim(), out int port) && port is >= 1024 and <= 65535)
                    {
                        s.WebPort = port;
                        ConfigStore.Save(s);
                        Audit.Log(Audit.Manage, "修改设置", $"Web 端口→{port}");
                        Console.WriteLine("[完成] 已保存（重启生效）。");
                    }
                    break;
                }
                case "5":
                    s.AutoOpenBrowser = !s.AutoOpenBrowser;
                    ConfigStore.Save(s);
                    Audit.Log(Audit.Manage, "修改设置", $"自动打开浏览器→{(s.AutoOpenBrowser ? "开" : "关")}");
                    Console.WriteLine($"[完成] 自动打开浏览器已{(s.AutoOpenBrowser ? "开启" : "关闭")}。");
                    break;
                case "6":
                    ChannelsMenu(ctx);
                    break;
                case "7":
                    ctx.History.Cleanup(s.HistoryRetentionDays);
                    Audit.Log(Audit.Manage, "清理过期历史", $"保留 {s.HistoryRetentionDays} 天");
                    break;
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

    private static void ChannelsMenu(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            AppSettings s = ctx.Settings;
            string[] options =
            {
                $"1. 通知渠道开关（Webhook {(s.WebhookEnabled ? "开" : "关")} / SMTP {(s.SmtpEnabled ? "开" : "关")}）",
                $"2. Webhook 地址（当前：{(string.IsNullOrWhiteSpace(s.WebhookUrl) ? "未设置" : "已设置")}）",
                $"3. Webhook 签名密钥（当前：{(SecretStore.TryDecrypt(s.WebhookSecret, out string? ws) && !string.IsNullOrWhiteSpace(ws) ? "已配置" : "未配置")}）",
                $"4. Webhook 类型（当前：{WebhookSender.TypeDisplay(s.WebhookType)}）",
                $"5. generic 自定义模板（当前：{(string.IsNullOrWhiteSpace(s.WebhookTemplate) ? "未设置" : "已设置")}）",
                $"6. SMTP 服务器（当前：{s.SmtpHost}:{s.SmtpPort} {s.SmtpSecure}）",
                $"7. SMTP 账号与授权码（当前：{s.SmtpUser} / {(SecretStore.TryDecrypt(s.SmtpPassword, out string? sp) && !string.IsNullOrWhiteSpace(sp) ? "已配置" : "未配置")}）",
                $"8. SMTP 收件人（当前：{(string.IsNullOrWhiteSpace(s.SmtpTo) ? "未设置" : s.SmtpTo)}）",
                "9. 发送测试通知",
                "0. 返回上级",
            };
            int width = options.Max(option => option.Length);
            var lines = new List<string>
            {
                new string('=', width),
                "通知渠道",
                new string('=', width),
            };
            lines.AddRange(options);
            lines.Add(new string('=', width));
            Ui.Block(lines);
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
                    SetSendStrategy(ctx);
                    break;
                case "2":
                    SetSecret(ctx, "webhookUrl");
                    break;
                case "3":
                    SetSecret(ctx, "webhookSecret");
                    break;
                case "4":
                    SetWebhookType(ctx);
                    break;
                case "5":
                {
                    (EditResult result, string value) = Ui.PromptEdit("请输入 generic 模板（JSON，{text} 为占位符，回车=不变，Esc=清空）：");
                    if (result == EditResult.Entered)
                    {
                        s.WebhookTemplate = value.Trim();
                        Audit.Log(Audit.Manage, "修改通知渠道", "generic 模板");
                    }
                    else if (result == EditResult.Clear)
                    {
                        s.WebhookTemplate = "";
                        Audit.Log(Audit.Manage, "修改通知渠道", "generic 模板已清空");
                    }
                    ConfigStore.Save(s);
                    Console.WriteLine("[完成] 已保存。");
                    break;
                }
                case "6":
                    SetSmtpServer(ctx);
                    break;
                case "7":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"SMTP 账号（当前：{s.SmtpUser}，回车=不变，Esc=清空）：");
                    if (result == EditResult.Entered)
                    {
                        s.SmtpUser = value.Trim();
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 账号");
                    }
                    else if (result == EditResult.Clear)
                    {
                        s.SmtpUser = "";
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 账号已清空");
                    }
                    SetSecret(ctx, "smtpPassword");
                    break;
                }
                case "8":
                {
                    (EditResult result, string value) = Ui.PromptEdit($"SMTP 收件人（逗号分隔，回车=不变，Esc=清空）：");
                    if (result == EditResult.Entered)
                    {
                        s.SmtpTo = value.Trim();
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 收件人");
                    }
                    else if (result == EditResult.Clear)
                    {
                        s.SmtpTo = "";
                        Audit.Log(Audit.Manage, "修改通知渠道", "SMTP 收件人已清空");
                    }
                    ConfigStore.Save(s);
                    Console.WriteLine("[完成] 已保存。");
                    break;
                }
                case "9":
                {
                    string text = $"[NexusPipeline] 通知测试\r\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n收到即配置正确。";
                    bool ok = NotifySender.SendAsync(s, text).GetAwaiter().GetResult();
                    Audit.Log(Audit.Manage, "发送测试通知", ok ? "成功" : "失败");
                    Console.WriteLine(ok ? "[OK] 测试通知发送成功。" : "[错误] 测试通知发送失败，详见日志。");
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

    private static void SetSendStrategy(RuntimeContext ctx)
    {
        string? webhookChoice = Ui.Prompt($"Webhook 开关（当前：{(ctx.Settings.WebhookEnabled ? "开" : "关")}，输入 1 开 / 2 关 / 回车不变）：");
        if (webhookChoice?.Trim() is "1" or "2")
        {
            ctx.Settings.WebhookEnabled = webhookChoice.Trim() == "1";
            Audit.Log(Audit.Manage, "修改通知渠道", $"Webhook 开关→{(ctx.Settings.WebhookEnabled ? "开" : "关")}");
        }
        string? smtpChoice = Ui.Prompt($"SMTP 开关（当前：{(ctx.Settings.SmtpEnabled ? "开" : "关")}，输入 1 开 / 2 关 / 回车不变）：");
        if (smtpChoice?.Trim() is "1" or "2")
        {
            ctx.Settings.SmtpEnabled = smtpChoice.Trim() == "1";
            Audit.Log(Audit.Manage, "修改通知渠道", $"SMTP 开关→{(ctx.Settings.SmtpEnabled ? "开" : "关")}");
        }
        ConfigStore.Save(ctx.Settings);
        Console.WriteLine($"[完成] Webhook {(ctx.Settings.WebhookEnabled ? "开" : "关")} / SMTP {(ctx.Settings.SmtpEnabled ? "开" : "关")}");
    }

    private static void SetWebhookType(RuntimeContext ctx)
    {
        Console.WriteLine("Webhook 类型：");
        Console.WriteLine("  1. feishu（飞书）  2. dingtalk（钉钉）  3. wecom（企业微信）");
        Console.WriteLine("  4. slack          5. discord           6. generic（自定义模板）");
        string? choice = Ui.Prompt("请选择（直接回车不变）：");
        string? value = choice?.Trim() switch
        {
            "1" => "feishu",
            "2" => "dingtalk",
            "3" => "wecom",
            "4" => "slack",
            "5" => "discord",
            "6" => "generic",
            _ => null,
        };
        if (value is null)
        {
            Console.WriteLine("[提示] 无效选项。");
            return;
        }
        ctx.Settings.WebhookType = value;
        ConfigStore.Save(ctx.Settings);
        Audit.Log(Audit.Manage, "修改通知渠道", $"Webhook 类型→{value}");
        Console.WriteLine($"[完成] Webhook 类型：{WebhookSender.TypeDisplay(value)}");
    }

    private static void SetSecret(RuntimeContext ctx, string key)
    {
        string label = key switch
        {
            "webhookUrl" => "Webhook 地址",
            "webhookSecret" => "Webhook 签名密钥",
            _ => "SMTP 授权码",
        };
        (EditResult result, string value) = Ui.PromptEditMasked($"请输入{label}（回车=不变，Esc=清空）：");
        if (result == EditResult.Entered)
        {
            ApplySecret(ctx.Settings, key, SecretStore.Encrypt(value));
            ConfigStore.Save(ctx.Settings);
            Audit.Log(Audit.Manage, "修改通知渠道", $"{label}已设置");
            Console.WriteLine($"[完成] {label}已加密保存（绑定当前电脑和用户）。");
        }
        else if (result == EditResult.Clear)
        {
            ApplySecret(ctx.Settings, key, "");
            ConfigStore.Save(ctx.Settings);
            Audit.Log(Audit.Manage, "修改通知渠道", $"{label}已清除");
            Console.WriteLine($"[完成] {label}已清除。");
        }
    }

    private static void ApplySecret(AppSettings settings, string key, string value)
    {
        switch (key)
        {
            case "webhookUrl":
                settings.WebhookUrl = value;
                break;
            case "webhookSecret":
                settings.WebhookSecret = value;
                break;
            case "smtpPassword":
                settings.SmtpPassword = value;
                break;
        }
    }

    private static void SetSmtpServer(RuntimeContext ctx)
    {
        AppSettings s = ctx.Settings;
        (EditResult hostResult, string hostValue) = Ui.PromptEdit($"SMTP 服务器地址（当前：{s.SmtpHost}，回车=不变，Esc=清空）：");
        if (hostResult == EditResult.Entered)
        {
            s.SmtpHost = hostValue.Trim();
        }
        else if (hostResult == EditResult.Clear)
        {
            s.SmtpHost = "";
        }
        (EditResult portResult, string portValue) = Ui.PromptEdit($"端口（当前：{s.SmtpPort}，回车=不变）：");
        if (portResult == EditResult.Entered && int.TryParse(portValue.Trim(), out int port) && port is >= 1 and <= 65535)
        {
            s.SmtpPort = port;
        }
        (EditResult secureResult, string secureValue) = Ui.PromptEdit($"加密方式（auto/ssl/starttls/none，当前：{s.SmtpSecure}，回车=不变）：");
        if (secureResult == EditResult.Entered && secureValue.Trim() is "auto" or "ssl" or "starttls" or "none")
        {
            s.SmtpSecure = secureValue.Trim();
        }
        ConfigStore.Save(s);
        Audit.Log(Audit.Manage, "修改通知渠道", $"SMTP 服务器={s.SmtpHost}:{s.SmtpPort} {s.SmtpSecure}");
        Console.WriteLine("[完成] 已更新 SMTP 服务器设置。");
    }

    public static void ShowStatus()
    {
        Ui.ClearScreen();
        RuntimeContext ctx = RuntimeContext.Instance;
        AppSettings s = ctx.Settings;
        Console.WriteLine("===== NexusPipeline 枢链 状态 =====");
        Console.WriteLine($"脚本实例：{ctx.Scripts.Count} 个 | 调度队列：{ctx.Queues.Count} 个");
        Console.WriteLine($"开机自启动：{(TaskRegistration.IsRegistered() ? "已注册" : "未注册")} | 轻量模式：{(s.LightweightMode ? "开" : "关")}");
        Console.WriteLine($"Web 界面：http://127.0.0.1:{s.WebPort}/（未检测是否运行）");
        Console.WriteLine();
        List<RunningExecution> active = ctx.Center.Active.ToList();
        if (active.Count == 0)
        {
            Console.WriteLine("当前没有正在运行的任务。");
        }
        else
        {
            Console.WriteLine($"正在运行（{active.Count}）：");
            foreach (RunningExecution exec in active)
            {
                Console.WriteLine($"  {exec.TargetName}（{exec.Kind}）当前：{exec.CurrentScriptName} {exec.CurrentStatus}");
            }
        }
        Console.WriteLine();
        (bool webhookOk, string webhookReason) = WebhookSender.Status(s);
        (bool smtpOk, string smtpReason) = SmtpSender.Status(s);
        Console.WriteLine($"通知渠道：Webhook {webhookReason} | SMTP {smtpReason}");
        Console.WriteLine($"渠道开关：Webhook {(s.WebhookEnabled ? "开" : "关")} / SMTP {(s.SmtpEnabled ? "开" : "关")}");
        Console.WriteLine();
        foreach (DispatchQueue queue in ctx.Queues)
        {
            Console.WriteLine($"队列「{queue.Name}」：{QueueRule.AutoRunModeDesc(queue.AutoRunMode)}，{queue.Tasks.Count} 个任务，完成操作={QueueRule.CompletionActionDesc(queue.CompletionAction)}");
            if (queue.AutoRunMode == "scheduled")
            {
                foreach (QueueTimeSet ts in queue.TimeSets.Where(ts => ts.Enabled))
                {
                    Console.WriteLine($"    定时：{DayDesc(ts.Days)} {ts.Time}");
                }
            }
        }
    }

    private static string DayDesc(List<int> days)
    {
        if (days.Count == 7)
        {
            return "每天";
        }
        string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return string.Join("/", days.OrderBy(day => day).Select(day => names[day]));
    }

    private static string? PromptText(string label, string current)
    {
        (EditResult result, string value) = Ui.PromptEdit($"{label}（当前：{(string.IsNullOrWhiteSpace(current) ? "空" : current)}，回车=不变，Esc=取消）：");
        if (result == EditResult.Clear)
        {
            return null;
        }
        return result == EditResult.Keep ? current : value.Trim();
    }
}
