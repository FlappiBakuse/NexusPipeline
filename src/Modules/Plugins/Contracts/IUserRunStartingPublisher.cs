using NexusPipeline.Plugin.Abstractions;
namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>执行域向 managed-code 插件发布用户开始事件的内部端口。</summary>
internal interface IUserRunStartingPublisher
{
    void Publish(PluginUserRunStartingEvent eventData);
}
