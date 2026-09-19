namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>配置候选与产生候选的插件输入名，避免展示层根据 inputs 再次猜测绑定语义。</summary>
internal sealed record ConfigInputCandidateSet(
    string InputName,
    IReadOnlyList<string> Values);
