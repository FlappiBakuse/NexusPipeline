using System.Text;
using Xunit;
using NexusPipeline.Modules.Execution;

namespace NexusPipeline.Tests.Execution;

public sealed class ResultCollectorTests
{
    [Fact]
    public void LogLimitCountsUtf8Bytes()
    {
        var collector = new ResultCollector();
        int charCount = (20 * 1024 * 1024 / 3) + 128;

        collector.Append(new string('汉', charCount));

        Assert.True(
            Encoding.UTF8.GetByteCount(collector.FullLog.ToString()) <= 20 * 1024 * 1024,
            "当前 ResultCollector 按 chars 而非 UTF-8 bytes 计数。");
    }
}
