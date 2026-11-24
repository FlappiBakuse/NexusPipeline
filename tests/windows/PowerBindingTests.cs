using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class PowerBindingTests
{
    [Theory(DisplayName = "The Windows Power Adapter Rejects Non Power Actions")]
    [InlineData(AutomationCompletionAction.None)]
    [InlineData(AutomationCompletionAction.Exit)]
    [InlineData((AutomationCompletionAction)99)]
    public void RejectsOtherActions(AutomationCompletionAction action)
        => Assert.Equal("completion_power_action_required",
            Assert.Throws<ArgumentException>(() => new WindowsCompletionCalls().Invoke(action)).Message);
}
