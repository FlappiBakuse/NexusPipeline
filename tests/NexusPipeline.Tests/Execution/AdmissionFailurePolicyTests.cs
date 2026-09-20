using Xunit;
using NexusPipeline.Modules.Execution;
namespace NexusPipeline.Tests.Execution;


public sealed class AdmissionFailurePolicyTests
{

    [Fact]
    public void ProcessConflictAdmission_IsTransientAndHasStableCode()
    {
        var failure = new ExecutionAdmissionFailure(
            ExecutionAdmissionFailureCode.ProcessConflict,
            "脚本进程仍在运行");

        Assert.Equal(AdmissionFailureDisposition.Transient, failure.Disposition);
        Assert.Equal("process_conflict", failure.StableCode);
    }
}
