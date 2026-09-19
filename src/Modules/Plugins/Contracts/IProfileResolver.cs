namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>数据化或 C# 插件提供的脚本 profile 推导能力。</summary>
internal interface IProfileResolver : IPluginCapability
{
    /// <summary>inputs 为脚本实例保存的用户输入值（key 为声明 name）；null 表示未提供。</summary>
    ScriptProfile? Resolve(string rootPath, IReadOnlyDictionary<string, string>? inputs);
}
