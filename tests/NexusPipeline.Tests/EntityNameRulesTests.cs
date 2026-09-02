using System.Text;
using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class EntityNameRulesTests
{
    private sealed class Item
    {
        public string Id { get; init; } = "";

        public int Index { get; init; }

        public string Name { get; set; } = "";
    }

    [Fact]
    public void NormalizeDuplicates_ReservesAllOriginalNames_AndKeepsStableOrder()
    {
        var items = new List<Item>
        {
            new() { Id = "first", Index = 0, Name = "ABC" },
            new() { Id = "second", Index = 1, Name = "abc" },
            new() { Id = "reserved", Index = 2, Name = "ABC-2" },
        };

        IReadOnlyList<NameNormalizationChange> changes = EntityNameRules.NormalizeDuplicates(
            items,
            item => item.Name,
            (item, name) => item.Name = name,
            item => item.Id,
            item => item.Index,
            64);

        Assert.Equal(new[] { "ABC", "abc-3", "ABC-2" }, items.Select(item => item.Name));
        Assert.Equal("second", Assert.Single(changes).EntityId);
        Assert.Equal("abc-3", changes[0].NewName);
    }

    [Fact]
    public void NormalizeDuplicates_IsIdempotentAfterFirstPass()
    {
        var items = new List<Item>
        {
            new() { Id = "a", Index = 0, Name = "名称" },
            new() { Id = "b", Index = 1, Name = "名称" },
        };

        EntityNameRules.NormalizeDuplicates(items, item => item.Name, (item, name) => item.Name = name, item => item.Id, item => item.Index, 64);
        IReadOnlyList<NameNormalizationChange> second = EntityNameRules.NormalizeDuplicates(items, item => item.Name, (item, name) => item.Name = name, item => item.Id, item => item.Index, 64);

        Assert.Empty(second);
        Assert.Equal(new[] { "名称", "名称-2" }, items.Select(item => item.Name));
    }

    [Fact]
    public void BuildSuffixedName_RespectsUtf8LimitWithoutSplittingRune()
    {
        string original = string.Concat(Enumerable.Repeat("😀", 20));
        string candidate = EntityNameRules.BuildSuffixedName(original, 2, 64);

        Assert.True(Encoding.UTF8.GetByteCount(candidate) <= 64);
        Assert.EndsWith("-2", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("�", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public void HasConflict_IsOrdinalIgnoreCaseAndSupportsSelfExclusion()
    {
        var items = new List<Item>
        {
            new() { Id = "a", Name = "Test" },
            new() { Id = "b", Name = "Other" },
        };

        Assert.True(EntityNameRules.HasConflict(items, "test", item => item.Name));
        Assert.False(EntityNameRules.HasConflict(items, "TEST", item => item.Name, item => item.Id == "a"));
    }
}
