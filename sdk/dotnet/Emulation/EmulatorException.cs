namespace NexusPipeline.Extensibility.Emulation;

/// <summary>A bounded vendor diagnostic; original tool output remains with its Host evidence owner.</summary>
public class EmulatorException : Exception
{
    /// <summary>The stable machine-readable vendor refusal.</summary>
    public string Code { get; }
    /// <summary>Rejects diagnostics that could contain paths, command output or arbitrary exception text.</summary>
    public EmulatorException(string code) : base(Require(code)) { Code = code; }
    private static string Require(string code) => code is { Length: > 0 and <= 128 } &&
        code.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? code :
        throw new ArgumentException("emulator_diagnostic_invalid", nameof(code));
}
