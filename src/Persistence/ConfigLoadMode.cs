namespace NexusPipeline.Persistence;

/// <summary>设置加载的持久化副作用策略。</summary>
internal enum ConfigLoadMode
{
    /// <summary>只读取并规范化内存对象；损坏文件保持原位。</summary>
    ReadOnly,

    /// <summary>宿主取得运行时所有权后的加载；允许保留损坏文件副本。</summary>
    Repair,
}
