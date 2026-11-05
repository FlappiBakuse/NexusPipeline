using System.Text.Json;
using NexusPipeline.Extensibility.Secrets;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class SecretMemoryTests
{
    [Fact(DisplayName = "Secret Draft Does Not Retain The Callers Buffer And Rejects Use After Disposal")]
    public void OwnsDraftLifetime()
    {
        var original = "fixture-only-token".ToCharArray();
        using var draft = new SecretDraft(original);
        Array.Clear(original);
        draft.Use(value => Assert.True(value.SequenceEqual("fixture-only-token")));
        Assert.DoesNotContain("fixture-only-token", draft.ToString());
        draft.Dispose();
        draft.Dispose();
        Assert.Throws<ObjectDisposedException>(() => draft.Use(_ => throw new IOException("Must not be called")));
    }

    [Fact(DisplayName = "Keep Set And Clear Have Distinct Semantics And Set Owns The Draft Lifetime")]
    public void SeparatesMutationSemantics()
    {
        using var keep = SecretChange.Keep();
        using var clear = SecretChange.Clear();
        var draft = new SecretDraft("fixture-only-token");
        using var set = SecretChange.Set(draft);
        Assert.Equal(SecretChangeKind.Keep, keep.Kind);
        Assert.Equal(SecretChangeKind.Clear, clear.Kind);
        Assert.Equal(SecretChangeKind.Set, set.Kind);
        Assert.Throws<InvalidOperationException>(() => keep.UseValue(_ => throw new IOException("Must not be called")));
        Assert.Throws<InvalidOperationException>(() => clear.UseValue(_ => throw new IOException("Must not be called")));
        set.UseValue(value => Assert.True(value.SequenceEqual("fixture-only-token")));
        set.Dispose();
        Assert.Throws<ObjectDisposedException>(() => draft.Use(_ => throw new IOException("Must not be called")));
        Assert.Throws<ObjectDisposedException>(() => set.UseValue(_ => throw new IOException("Must not be called")));
    }

    [Fact(DisplayName = "Secret Memory Cannot Enter Ordinary Json Dtos Or Be Recreated By Their Deserializer")]
    public void RejectsJsonSerialization()
    {
        using var draft = new SecretDraft("fixture-only-token");
        using var change = SecretChange.Set(draft);
        var draftFailure = Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new { secret = draft }));
        var changeFailure = Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new { secret = change }));
        Assert.DoesNotContain("fixture-only-token", draftFailure.ToString());
        Assert.DoesNotContain("fixture-only-token", changeFailure.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SecretDraft>("{}"));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SecretChange>("{}"));
    }

    [Theory(DisplayName = "Invalid Secret Sizes Cannot Implicitly Become Keep Or Clear")]
    [InlineData(0)]
    [InlineData(65537)]
    public void RejectsInvalidSizes(int size) =>
        Assert.Throws<ArgumentException>(() => new SecretDraft(new char[size]));
}
