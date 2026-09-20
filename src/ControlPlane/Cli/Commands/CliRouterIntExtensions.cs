using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.ControlPlane.Cli;
namespace NexusPipeline.ControlPlane.Cli.Commands;


internal static class CliRouterIntExtensions
{
    public static int AlsoWriteUsage(this int result)
    {
        if (!CliOutput.MachineMode)
        {
            Console.WriteLine(CliText.Get("error.usage", "使用 nexus-pipeline.exe --help 查看命令帮助。"));
        }
        return result;
    }
}
