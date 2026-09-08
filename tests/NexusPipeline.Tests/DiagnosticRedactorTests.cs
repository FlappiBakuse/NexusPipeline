using NexusPipeline.Services.Diagnostics;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class DiagnosticRedactorTests
{
    [Fact]
    public void RedactText_RemovesBearerAssignmentsUrlsAndUserPaths()
    {
        const string raw = "Authorization: Bearer abc123 token=top-secret webhook=https://example.test/webhook/secret C:\\Users\\Alice\\config.json";

        string redacted = DiagnosticRedactor.RedactText(raw);

        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("top-secret", redacted);
        Assert.DoesNotContain("webhook/secret", redacted);
        Assert.DoesNotContain("C:\\Users\\Alice", redacted);
        Assert.Contains("<REDACTED>", redacted);
        Assert.Contains("<REDACTED_URL>", redacted);
        Assert.Contains("<USER_PATH>", redacted);
        Assert.False(DiagnosticRedactor.ContainsSecretLikeValue(redacted));
    }

    [Fact]
    public void SecretCanaryDetectsUnredactedValuesAndAcceptsRedactedPlaceholders()
    {
        Assert.True(DiagnosticRedactor.ContainsSecretLikeValue("{\"password\":\"plain-text\"}"));
        Assert.True(DiagnosticRedactor.ContainsSecretLikeValue("Bearer plain-text"));
        Assert.False(DiagnosticRedactor.ContainsSecretLikeValue("{\"password\":\"<REDACTED>\"}"));
    }
}
