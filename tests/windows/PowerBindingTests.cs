using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class PowerBindingTests
{
    [Theory(DisplayName = "The Actual Power Adapter Rejects Non Power Actions Before Opening Any Native Privilege")]
    [InlineData(AutomationCompletionAction.None)]
    [InlineData(AutomationCompletionAction.Exit)]
    [InlineData((AutomationCompletionAction)99)]
    public void RejectsOtherActions(AutomationCompletionAction action)
        => Assert.Throws<ArgumentException>(() => new WindowsCompletionCalls().Invoke(action));
}
