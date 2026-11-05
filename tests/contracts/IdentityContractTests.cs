using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Extensibility.Secrets;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class IdentityContractTests
{
    [Theory(DisplayName = "Pure Credential Rotation Receives Owned Temporary Drafts And Remains Bound To Its Published Registration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnsRotationMemory(bool allowed)
    {
        var fixture = new ScheduledContractFixture(); var handler = new Handler { RotationAllowed = allowed }; fixture.HandlerOverride = handler;
        var scope = await fixture.ActivateAsync(); var entry = fixture.Registry.RequireManualEntry("fixture", "daily");
        using var before = new SecretValue("synthetic-before"); using var after = new SecretValue("synthetic-after");
        Assert.Equal(allowed, entry.CanRotateIdentity("account", before, after));
        Assert.Throws<ObjectDisposedException>(() => handler.RotationBefore!.Use(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => handler.RotationAfter!.Use(_ => { }));
        before.Use(value => Assert.True(value.SequenceEqual("synthetic-before")));
        after.Use(value => Assert.True(value.SequenceEqual("synthetic-after")));
        await scope.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => entry.CanRotateIdentity("account", before, after));
    }

    [Fact(DisplayName = "Registered Identity Projection Owns Only Bounded Secret Memory And Cannot Outlive Its Exact Activation")]
    public async Task ProjectsOwnedMaterial()
    {
        var fixture = new ScheduledContractFixture(); var handler = new Handler(); fixture.HandlerOverride = handler;
        var scope = await fixture.ActivateAsync();
        var entry = fixture.Registry.RequireManualEntry("fixture", "daily");
        Assert.Equal("v1", entry.RequireIdentity());
        using var material = new SecretValue("synthetic-auth-with-display-metadata");
        using var identity = entry.ProjectIdentity("account", material);
        identity.Use(value => Assert.True(value.SequenceEqual("canonical-auth-only")));
        Assert.Throws<ObjectDisposedException>(() => handler.Input!.Use(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => handler.Output!.Use(_ => { }));
        material.Use(value => Assert.True(value.SequenceEqual("synthetic-auth-with-display-metadata")));
        await scope.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => entry.ProjectIdentity("account", material));
        Assert.Equal(1, handler.Calls);
    }

    [Theory(DisplayName = "Unavailable Or Oversized Identity Projections Cannot Become A Durable Credential Family")]
    [InlineData("unavailable")]
    [InlineData("version")]
    [InlineData("oversized")]
    [InlineData("blank")]
    public async Task RejectsInvalidProjection(string fault)
    {
        var fixture = new ScheduledContractFixture(); var handler = new Handler { Fault = fault }; fixture.HandlerOverride = handler;
        await using var scope = await fixture.ActivateAsync();
        var entry = fixture.Registry.RequireManualEntry("fixture", "daily");
        using var material = new SecretValue("synthetic-auth");
        if (fault is "unavailable" or "version") Assert.Throws<UnauthorizedAccessException>(() => entry.RequireIdentity());
        else Assert.Throws<IOException>(() => entry.ProjectIdentity("account", material));
        if (handler.Output is not null) Assert.Throws<ObjectDisposedException>(() => handler.Output.Use(_ => { }));
    }

    private sealed class Handler : IManualWorkHandler
    {
        internal string? Fault; internal int Calls;
        internal SecretDraft? Input, Output;
        internal SecretDraft? RotationBefore, RotationAfter;
        internal bool RotationAllowed;
        public bool CanRotateIdentity(string field, SecretDraft before, SecretDraft after)
        { Assert.Equal("account", field); RotationBefore = before; RotationAfter = after; return RotationAllowed; }
        public string? IdentityVersion => Fault == "unavailable" ? null : Fault == "version" ? "V 1" : "v1";
        public SecretDraft ProjectIdentity(string field, SecretDraft actualStoredMaterial)
        {
            Assert.Equal("account", field); Calls++; Input = actualStoredMaterial;
            return Output = new(Fault == "oversized" ? new string('a', 16385) : Fault == "blank" ? "  " : "canonical-auth-only");
        }
        public ValueTask<ManualWorkTask?> ReadTaskAsync(Guid taskId, CancellationToken cancellationToken) => ValueTask.FromResult<ManualWorkTask?>(null);
        public ValueTask<ScheduledWorkResult> ExecuteAsync(ScheduledInvocation invocation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ScheduledWorkResult> ExecuteManualAsync(ManualWorkInvocation invocation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
