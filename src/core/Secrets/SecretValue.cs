namespace NexusPipeline.Core.Secrets;

public delegate void SecretValueReader(ReadOnlySpan<char> value);

public sealed class SecretValue : IDisposable
{
    private readonly object _gate = new();
    private char[]? _value;
    public SecretValue(ReadOnlySpan<char> value)
    {
        if (value.Length is < 1 or > 65536) throw new ArgumentException("secret_value_size_invalid");
        _value = value.ToArray();
    }
    public void Use(SecretValueReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_value is null, this);
            reader(_value);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_value is null) return;
            Array.Clear(_value);
            _value = null;
        }
    }
    public override string ToString() => "[secret value]";
}
