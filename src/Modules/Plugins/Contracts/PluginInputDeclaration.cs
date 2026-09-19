namespace NexusPipeline.Modules.Plugins.Contracts;


/// <summary>resolve.json 声明的用户输入变量：脚本实例按声明提供值，推导时以 {"{"}input:名称{"}"} 内联替换。</summary>
internal sealed class PluginInputDeclaration
{
    public string Name { get; init; } = "";

    public string Label { get; init; } = "";

    /// <summary>可选的宿主插件词典 key；缺失时使用 Label 作为回退。</summary>
    public string LabelKey { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>可选的宿主插件词典 key；缺失时使用 Description 作为回退。</summary>
    public string DescriptionKey { get; init; } = "";

    public string Default { get; init; } = "";

    public bool Required { get; init; }

    public string Pattern { get; init; } = "";
}
