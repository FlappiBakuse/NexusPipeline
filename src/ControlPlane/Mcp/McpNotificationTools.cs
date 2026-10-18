using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NexusPipeline.Modules.Notifications;

namespace NexusPipeline.ControlPlane.Mcp;

internal sealed partial class McpReadOnlyTools
{
    [McpServerTool(Name = "list_notifications", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("列出脱敏通知任务，包含稳定 ID、顺序、类型、启用和配置状态。")]
    public CallToolResult ListNotifications() => _context.Notifications.BlockedReason is { } reason
        ? McpToolResult.Failure(reason, reason) : McpToolResult.Success(_context.Notifications.List());

    [McpServerTool(Name = "get_notification", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("读取指定稳定 ID 的通知任务，不返回密码、Webhook 地址或 Token 明文。")]
    public CallToolResult GetNotification(string id) => _context.Notifications.Find(id) is { } task
        ? McpToolResult.Success(task) : McpToolResult.Failure("notification_not_found", "通知任务不存在");
}

internal sealed partial class McpMutationTools
{
    [McpServerTool(Name = "create_notification", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("创建 SMTP 或 Webhook 通知任务，受 MaxNotificationTasks 限制；密钥通过 secrets 中的 keep/replace/clear 操作写入。")]
    public CallToolResult CreateNotification(NotificationTaskInput task) => McpToolResult.From(_context.Notifications.Save(null, task));

    [McpServerTool(Name = "update_notification", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("保存通知任务完整配置；任务类型不能改变，未提供密钥操作时保留已存密钥。")]
    public CallToolResult UpdateNotification(string id, NotificationTaskInput task) => McpToolResult.From(_context.Notifications.Save(id, task));

    [McpServerTool(Name = "delete_notification", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("删除通知任务。已有用户、队列和签到选择会显示失效引用，后续发送跳过此目标。")]
    public CallToolResult DeleteNotification(string id) => McpToolResult.From(_context.Notifications.Delete(id));

    [McpServerTool(Name = "order_notifications", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("按完整且无重复的任务 ID 列表保存通知任务顺序。")]
    public CallToolResult OrderNotifications(string[] ids) => McpToolResult.From(_context.Notifications.Reorder(ids));

    [McpServerTool(Name = "test_notification", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(McpToolEnvelope))]
    [Description("实际向已保存任务的目标发送一条测试通知，可能发送邮件或外部 Webhook 消息；不会改变启用状态。")]
    public async Task<CallToolResult> TestNotification(string id, CancellationToken cancellationToken) =>
        McpToolResult.From(await _context.NotificationDelivery.TestAsync(id, cancellationToken).ConfigureAwait(false));
}
