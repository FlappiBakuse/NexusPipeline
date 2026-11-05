using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationSpanNode(int start, int end, JsonNode? value,
    Dictionary<string, ConfigurationSpanNode>? properties = null, List<ConfigurationSpanNode>? items = null)
{
    internal int Start { get; } = start;
    internal int End { get; } = end;
    internal JsonNode? Value { get; } = value;
    internal Dictionary<string, ConfigurationSpanNode>? Properties { get; } = properties;
    internal List<ConfigurationSpanNode>? Items { get; } = items;

    internal ResolvedConfigurationNode Resolve(ConfigurationSelector selector)
    {
        var current = this;
        var path = new List<object>();
        foreach (var step in selector.Steps)
        {
            if (step is ConfigurationProperty property && current.Properties is { } properties && properties.TryGetValue(property.Name, out var child))
            { current = child; path.Add(property.Name); }
            else if (step is ConfigurationIndex index && current.Items is { } items && index.Index < items.Count)
            { current = items[index.Index]; path.Add(index.Index); }
            else if (step is ConfigurationMatch match && current.Items is { } matches)
            {
                var selected = matches.Select((node, index) => (node, index)).Where(pair =>
                    pair.node.Properties?.TryGetValue(match.Property, out var id) == true &&
                    id.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == match.Value).ToArray();
                if (selected.Length != 1) throw new ConfigurationException(selected.Length == 0
                    ? "configuration_selector_not_found" : "configuration_selector_ambiguous");
                current = selected[0].node; path.Add(selected[0].index);
            }
            else throw new ConfigurationException("configuration_selector_not_found");
        }
        return new(current, path.AsReadOnly());
    }

    internal ResolvedConfigurationNode Missing(ConfigurationSelector selector)
    {
        if (selector.Steps.LastOrDefault() is not ConfigurationProperty property)
            throw new ConfigurationException("configuration_patch_absence_invalid");
        var parent = Resolve(new(selector.Steps.RemoveAt(selector.Steps.Length - 1)));
        if (parent.Node.Properties is not { } properties) throw new ConfigurationException("configuration_selector_not_found");
        if (properties.ContainsKey(property.Name)) throw new ConfigurationException("configuration_patch_expected_mismatch");
        return new(parent.Node, parent.Path.Append<object>(property.Name).ToArray());
    }
}

internal sealed record ResolvedConfigurationNode(ConfigurationSpanNode Node, IReadOnlyList<object> Path);

internal static class ConfigurationSpanAccess
{
    internal static void Require(ConfigurationSpanNode root, ConfigurationSelector selector, ResolvedConfigurationNode target,
        IReadOnlyList<ConfigurationAccessGrant> grants, IReadOnlyList<ConfigurationSelector> protectedSelectors)
    {
        var allowed = false;
        foreach (var grant in grants)
        {
            try
            {
                var path = ResolveGrant(root, grant.Selector);
                allowed |= Prefix(path, target.Path) && (grant.Descendants || path.Count == target.Path.Count);
            }
            catch (ConfigurationException error) when (error.Message == "configuration_selector_not_found") { }
        }
        if (!allowed) throw new ConfigurationException("configuration_access_denied");
        foreach (var denied in protectedSelectors)
        {
            try
            {
                var path = root.Resolve(denied).Path;
                if (Prefix(path, target.Path) || Prefix(target.Path, path)) throw new ConfigurationException("configuration_access_denied");
            }
            catch (ConfigurationException error) when (error.Message == "configuration_selector_not_found")
            {
                if (MayOverlap(denied, selector)) throw new ConfigurationException("configuration_access_denied");
            }
        }
    }

    private static IReadOnlyList<object> ResolveGrant(ConfigurationSpanNode root, ConfigurationSelector selector)
    {
        try { return root.Resolve(selector).Path; }
        catch (ConfigurationException error) when (error.Message == "configuration_selector_not_found")
        { return root.Missing(selector).Path; }
    }

    internal static bool Prefix(IReadOnlyList<object> parent, IReadOnlyList<object> child) =>
        parent.Count <= child.Count && parent.Select((step, index) => Equals(step, child[index])).All(equal => equal);

    private static bool MayOverlap(ConfigurationSelector left, ConfigurationSelector right)
    {
        for (var index = 0; index < Math.Min(left.Steps.Length, right.Steps.Length); index++)
        {
            var a = left.Steps[index]; var b = right.Steps[index];
            if (a is ConfigurationProperty pa && b is ConfigurationProperty pb && pa.Name != pb.Name ||
                a is ConfigurationIndex ia && b is ConfigurationIndex ib && ia.Index != ib.Index ||
                a is ConfigurationMatch ma && b is ConfigurationMatch mb && ma.Property == mb.Property && ma.Value != mb.Value)
                return false;
            if ((a is ConfigurationProperty) != (b is ConfigurationProperty)) return false;
        }
        return true;
    }
}
