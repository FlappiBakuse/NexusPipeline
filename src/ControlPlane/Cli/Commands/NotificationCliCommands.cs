namespace NexusPipeline.ControlPlane.Cli.Commands;

internal static partial class CliCommandRouter
{
    private static int ExecuteNotification(CliArguments args)
    {
        string sub = Positional(args, 1)?.ToLowerInvariant() ?? "list";
        var client = new CliApiClient();
        if (sub == "list")
            return EnsurePositionals(args, Positional(args, 1) is null ? 1 : 2, "notification list") && EnsureOptions(args)
                ? ReturnApi(client.Get("/api/notifications")) : CliExitCodes.For("invalid_arguments");
        if (sub is "create" or "order")
        {
            if (!EnsurePositionals(args, 2, "notification " + sub)) return CliExitCodes.For("invalid_arguments");
            return sub == "create" ? SendFileMutation(client, args, "POST", "/api/notifications", "通知任务")
                : SendIds(client, args, "/api/notifications/order", "通知任务");
        }
        if (sub is not ("get" or "update" or "delete" or "test"))
            return CliOutput.WriteFailure("invalid_arguments", "notification: list/get/create/update/delete/order/test");
        if (!EnsurePositionals(args, 3, "notification " + sub)) return CliExitCodes.For("invalid_arguments");
        if (!TryRequirePositional(args, 2, "通知任务 ID 或名称", out string reference, out int error)
            || !TryResolveTarget(client, "/api/notifications", reference, "通知任务", out string id, out error)) return error;
        string route = $"/api/notifications/{Escape(id)}";
        if (sub == "update") return SendFileMutation(client, args, "PUT", route, "通知任务");
        if (!EnsureOptions(args)) return CliExitCodes.For("invalid_arguments");
        return sub switch
        {
            "get" => ReturnApi(client.Get(route)),
            "delete" => ReturnApi(client.Delete(route)),
            _ => ReturnApi(client.Post(route + "/test"))
        };
    }
}
