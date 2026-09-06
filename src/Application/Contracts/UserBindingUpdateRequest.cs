using NexusPipeline.Models;

namespace NexusPipeline.App.Contracts;

/// <summary>用户绑定更新请求；ConfigInputsSpecified 区分字段缺失与显式清空。</summary>
internal sealed record UserBindingUpdateRequest(
    UserScriptBinding Binding,
    bool ConfigInputsSpecified);
