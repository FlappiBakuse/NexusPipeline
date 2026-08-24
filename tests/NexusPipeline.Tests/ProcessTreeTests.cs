using NexusPipeline.Utilities;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>进程树收集与游戏进程排除逻辑（v0.6.5+）：脚本自启动的游戏进程（进程名与 GameExe 一致）
/// 不被视为脚本树成员，树清理时排除其整棵子树。</summary>
public class ProcessTreeTests
{
    private static Dictionary<int, SystemActions.ProcessNode> Nodes(params (int Pid, int Ppid, string Exe)[] items)
    {
        var dict = new Dictionary<int, SystemActions.ProcessNode>();
        foreach ((int pid, int ppid, string exe) in items)
        {
            dict[pid] = new SystemActions.ProcessNode(pid, ppid, exe);
        }
        return dict;
    }

    [Fact]
    public void CollectTree_NoExclude_CollectsAll()
    {
        // 根 100 → 101 → 102（链）
        var nodes = Nodes((100, 0, "script.exe"), (101, 100, "helper.exe"), (102, 101, "game.exe"));
        HashSet<int> tree = SystemActions.CollectTree(100, nodes, null);
        Assert.Equal(new[] { 100, 101, 102 }, tree.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CollectTree_ExcludeGame_SkipsGameSubtree()
    {
        // 脚本 100 → 中间 101 → 游戏 102（父是脚本树成员，但名与 GameExe 一致）→ 游戏子 103
        var nodes = Nodes((100, 0, "script.exe"), (101, 100, "helper.exe"), (102, 101, "GenshinImpact.exe"), (103, 102, "crashpad.exe"));
        HashSet<int> tree = SystemActions.CollectTree(100, nodes, "GenshinImpact");
        Assert.Equal(new[] { 100, 101 }, tree.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CollectTree_ExcludeGame_WhenRootIsGame_RetainsRootAndItsNonGameChildren()
    {
        var nodes = Nodes((100, 0, "GenshinImpact.exe"), (101, 100, "child.exe"));
        HashSet<int> tree = SystemActions.CollectTree(100, nodes, "genshinimpact");
        Assert.Equal(new[] { 100, 101 }, tree.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CollectTree_ExcludeName_IsCaseInsensitive()
    {
        var nodes = Nodes((100, 0, "script.exe"), (101, 100, "StarRail.exe"), (102, 101, "x.exe"));
        HashSet<int> tree = SystemActions.CollectTree(100, nodes, "starrail");
        Assert.Equal(new[] { 100 }, tree.ToArray());
    }

    [Fact]
    public void CollectTree_ExcludeDoesNotAffectOtherNames()
    {
        var nodes = Nodes((100, 0, "script.exe"), (101, 100, "helper.exe"), (102, 101, "othergame.exe"));
        HashSet<int> tree = SystemActions.CollectTree(100, nodes, "GenshinImpact");
        Assert.Equal(new[] { 100, 101, 102 }, tree.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CollectTree_BranchyTree_ExcludesOnlyMatchingBranch()
    {
        // 根 100 → 游戏 101（排除）+ 根 100 → 102 → 103（保留）
        var nodes = Nodes((100, 0, "script.exe"), (101, 100, "game.exe"), (102, 100, "branch.exe"), (103, 102, "leaf.exe"));
        HashSet<int> tree = SystemActions.CollectTree(100, nodes, "game");
        Assert.Equal(new[] { 100, 102, 103 }, tree.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CollectTree_MissingRoot_ReturnsEmpty()
    {
        var nodes = Nodes((101, 100, "child.exe"));
        HashSet<int> tree = SystemActions.CollectTree(999, nodes, null);
        Assert.Empty(tree);
    }
}
