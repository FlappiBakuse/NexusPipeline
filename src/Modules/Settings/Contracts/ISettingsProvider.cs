using NexusPipeline.Modules.Settings;
namespace NexusPipeline.Modules.Settings.Contracts;


/// <summary>设置读取端口，避免业务服务为读取设置而反向依赖 HostCompositionRoot。</summary>
internal interface ISettingsProvider
{
    AppSettings Current { get; }
}
