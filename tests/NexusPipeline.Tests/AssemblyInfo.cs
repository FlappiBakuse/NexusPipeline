using Xunit;

// HostTestScope 构造真实 Host 组合根（含全局 AppPaths/单实例相关静态状态），
// 各测试类并行会互相污染；保留集约 105 个用例，串行总耗时仍在秒级。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
