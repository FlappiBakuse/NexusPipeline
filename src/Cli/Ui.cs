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
        Console.Write(label);
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
                Console.Write(key.KeyChar);
            }
        }
    }

    public static (EditResult Result, string Value) PromptEditMasked(string label)
    {
        Console.Write(label);
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
                Console.Write('*');
            }
        }
    }

    public static bool IsYes(string? answer)
    {
        return answer is not null && answer.Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>菜单保存兜底（v0.6.6+）：执行保存动作，IO 异常时提示且不退出菜单；返回是否保存成功。</summary>
    public static bool TrySave(Action save, string what)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] {what}保存失败：{ex.Message}（本次修改未落盘）");
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
        (EditResult result, string value) = PromptEdit($"{label}（当前：{(string.IsNullOrWhiteSpace(current) ? "空" : current)}，回车=不变，Esc=取消）：");
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
            return "每天";
        }
        string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return string.Join("/", days.OrderBy(day => day).Select(day => names[day]));
    }
}
