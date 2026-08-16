using System.Globalization;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;

namespace NexusPipeline.Cli;

/// <summary>调度队列管理子菜单：列表 / 新建 / 编辑（自动运行方式、完成操作、定时列表、任务列表）/ 删除。</summary>
internal static class QueuesMenu
{
    public static void Show(RuntimeContext ctx)
    {
        while (true)
        {
            Ui.ClearScreen();
            var ordered = ctx.Queues.OrderBy(queue => queue.Index).ToList();
            var lines = new List<string>();
            foreach (DispatchQueue queue in ordered)
            {
                string tasks = string.Join(" → ", queue.Tasks.OrderBy(task => task.Index).Select(task =>
                {
                    ScriptInstance? script = ctx.FindScript(task.ScriptInstanceId);
                    return script?.Name ?? "(缺失)";
                }));
                lines.Add($"{lines.Count + 1}. {queue.Name} | {QueueRule.AutoRunModeDesc(queue.AutoRunMode)} | 完成操作：{QueueRule.CompletionActionDesc(queue.CompletionAction)} | 任务：{(tasks.Length > 0 ? tasks : "无")}");
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
                        EditQueue(ctx, ordered[index - 1]);
                    }
                    else
                    {
                        DispatchQueue removing = ordered[index - 1];
                        string? answer = Ui.Prompt($"确定删除调度队列「{removing.Name}」吗？(Y/N)：");
                        if (Ui.IsYes(answer))
                        {
                            string removedName = removing.Name;
                            // v0.7.4（KN-05）：运行中队列拒绝删除，避免运行状态悬挂在运行面板。
                            bool queueRunning = ctx.Center.Active.Any(exec => exec.Kind == "queue" && exec.TargetId == removing.Id);
                            if (queueRunning)
                            {
                                Console.WriteLine("[错误] 调度队列正在运行中，无法删除。");
                            }
                            else if (Ui.TrySave(() =>
                            {
                                ctx.Queues.Remove(removing);
                                DataStore.SaveQueues(ctx.Queues);
                            }, "调度队列"))
                            {
                                Audit.Log(Audit.Manage, "删除调度队列", removedName);
                                Console.WriteLine("[完成] 已删除。");
                            }
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

        string? name = Ui.PromptText("队列名称", queue.Name);
        if (name is null)
        {
            return;
        }
        queue.Name = name;

        Console.WriteLine("自动运行方式：1=启动时运行 2=定时运行 3=不运行");
        string? mode = Ui.PromptText("选择", queue.AutoRunMode switch
        {
            "startup" => "1",
            "scheduled" => "2",
            _ => "3",
        });
        if (mode is not null)
        {
            queue.AutoRunMode = mode switch { "1" => "startup", "2" => "scheduled", "3" => "none", _ => queue.AutoRunMode };
        }

        Console.WriteLine("完成操作：1=无操作 2=退出软件 3=休眠 4=重启 5=关机");
        string? action = Ui.PromptText("选择", queue.CompletionAction switch
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

        string? notify = Ui.PromptText("队列级通知（开启后统一发送所有脚本状态，覆盖实例级设置；1=是 0=否）", queue.NotifyEnabled ? "1" : "0");
        if (notify is not null)
        {
            queue.NotifyEnabled = notify == "1";
        }

        if (queue.AutoRunMode == "scheduled")
        {
            EditTimeSets(queue);
        }

        EditTasks(ctx, queue);

        string? limitError = current is null ? Limits.CheckQueueCount(ctx.Queues.Count) : null;
        limitError ??= Limits.CheckNameBytes(queue.Name, Limits.Current.MaxQueueNameBytes, "队列名称");
        limitError ??= Limits.CheckTimeSets(queue.TimeSets.Count);
        limitError ??= Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, queue));
        limitError ??= Limits.CheckQueueMix(ctx, queue);
        if (limitError is not null)
        {
            Console.WriteLine($"[错误] {limitError}，未保存。");
            return;
        }

        if (Ui.TrySave(() =>
        {
            if (current is null)
            {
                if (ctx.Queues.Count > 0)
                {
                    queue.Index = ctx.Queues.Max(item => item.Index) + 1;
                }
                ctx.Queues.Add(queue);
            }
            else
            {
                ctx.Queues[ctx.Queues.IndexOf(current)] = queue;
            }
            DataStore.SaveQueues(ctx.Queues);
        }, "调度队列"))
        {
            Audit.Log(Audit.Manage, current is null ? "添加调度队列" : "修改调度队列", $"{queue.Name}（任务 {queue.Tasks.Count} 项）");
            Console.WriteLine("[完成] 调度队列已保存。");
        }
    }

    private static void EditTimeSets(DispatchQueue queue)
    {
        Console.WriteLine();
        Console.WriteLine("===== 定时列表（当前 {0} 条） =====", queue.TimeSets.Count);
        Console.WriteLine("1. 添加定时  2. 删除定时（输入序号）  回车=跳过");
        for (int i = 0; i < queue.TimeSets.Count; i++)
        {
            QueueTimeSet ts = queue.TimeSets[i];
            Console.WriteLine($"  {i + 1}. [{Ui.DayDesc(ts.Days)}] {ts.Time} {(ts.Enabled ? "启用" : "停用")}");
        }
        string? choice = Ui.Prompt("选择：");
        if (string.IsNullOrWhiteSpace(choice) || choice.Trim() == "0")
        {
            return;
        }
        if (choice.Trim() == "1")
        {
            string? timeLimit = Limits.CheckTimeSets(queue.TimeSets.Count + 1);
            if (timeLimit is not null)
            {
                Console.WriteLine($"[错误] {timeLimit}");
                return;
            }
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
                    ScriptInstance target = ctx.Scripts[scriptIndex - 1];
                    int newTotal = Limits.QueueTotalUsers(ctx, queue) + Math.Max(1, target.Users.Count(user => user.Enabled));
                    if (newTotal > Limits.Current.MaxQueueTotalUsers)
                    {
                        Console.WriteLine($"[错误] 任务列表的启用用户总数已达上限（{newTotal}/{Limits.Current.MaxQueueTotalUsers}）");
                        continue;
                    }
                    queue.Tasks.Add(new QueueTask
                    {
                        Index = queue.Tasks.Count,
                        ScriptInstanceId = target.Id,
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
}
