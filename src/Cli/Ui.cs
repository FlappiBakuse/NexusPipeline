using System.Text;

namespace NexusPipeline.Cli;

internal enum EditResult
{
    Entered,
    Keep,
    Clear
}

/// <summary>命令行交互基础工具：提示、编辑、清屏、公共输入辅助。</summary>
internal static class Ui
{
    public static void Block(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }
    }

    public static string? Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine();
    }

    public static (EditResult Result, string Value) PromptEdit(string label)
    {
        return PromptEditCore(label, masked: false);
    }

    public static (EditResult Result, string Value) PromptEditMasked(string label)
    {
        return PromptEditCore(label, masked: true);
    }

    /// <summary>行编辑通用实现（合并）：Esc=放弃（Clear），回车空=不变（Keep）；masked=true 时输入显示为 *。</summary>
    private static (EditResult Result, string Value) PromptEditCore(string label, bool masked)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)
        {
            // ：输入重定向（管道/自动化）时 ReadKey 抛 InvalidOperationException（Cannot read keys...），
            // 降级 ReadLine——空行/null = 不变（与「回车=不变」语义一致），非空 = 输入值；Esc 清空仅交互终端可用。
            string? line = Console.ReadLine();
            return string.IsNullOrEmpty(line) ? (EditResult.Keep, "") : (EditResult.Entered, line);
        }
        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return (EditResult.Clear, "");
            }
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                if (sb.Length == 0)
                {
                    return (EditResult.Keep, "");
                }
                return (EditResult.Entered, sb.ToString());
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write(masked ? '*' : key.KeyChar);
            }
        }
    }

    public static bool IsYes(string? answer)
    {
        return answer is not null && answer.Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>菜单保存兜底：执行保存动作，IO 异常时提示且不退出菜单；返回是否保存成功。</summary>
    public static bool TrySave(Action save, string what)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(CliText.Get(
                "error.save_failed",
                "{what}保存失败：{detail}（本次修改未落盘）",
                ("what", what),
                ("detail", ex.Message)));
            return false;
        }
    }

    public static void ClearScreen()
    {
        try
        {
            Console.Clear();
        }
        catch
        {
        }
    }

    /// <summary>带默认值/取消的文本编辑：Esc=取消（返回 null），回车空=保持当前值。</summary>
    public static string? PromptText(string label, string current)
    {
        string currentText = string.IsNullOrWhiteSpace(current)
            ? CliText.Get("value.empty", "空")
            : current;
        (EditResult result, string value) = PromptEdit(CliText.Get(
            "prompt.edit",
            "{label}（当前：{current}，回车=不变，Esc=取消）：",
            ("label", label),
            ("current", currentText)));
        if (result == EditResult.Clear)
        {
            return null;
        }
        return result == EditResult.Keep ? current : value.Trim();
    }

    public static string DayDesc(List<int> days)
    {
        if (days.Count == 7)
        {
            return CliText.Get("day.every", "每天");
        }
        string[] names =
        {
            CliText.Get("day.sun", "周日"),
            CliText.Get("day.mon", "周一"),
            CliText.Get("day.tue", "周二"),
            CliText.Get("day.wed", "周三"),
            CliText.Get("day.thu", "周四"),
            CliText.Get("day.fri", "周五"),
            CliText.Get("day.sat", "周六"),
        };
        return string.Join("/", days.OrderBy(day => day).Select(day => names[day]));
    }
}
