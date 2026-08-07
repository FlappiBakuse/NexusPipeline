using System.Text;

namespace NexusPipeline;

public enum EditResult
{
    Entered,
    Keep,
    Clear
}

public static class Ui
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
}
