using System.Text;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

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
