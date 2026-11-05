using System.Security.Cryptography;
using System.Text;
using Jint;

namespace NexusPipeline.Platform.Extensions;

public sealed class BoundedTaskScript
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _source;

    public BoundedTaskScript(string source, string expectedSha256)
    {
        var bytes = Utf8.GetBytes(source);
        if (bytes.Length is < 1 or > 262144 ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedSha256)
            throw new IOException("task_script_identity_or_size_invalid");
        _source = source;
    }

    public byte[] Execute(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        => Execute(request, cancellationToken, null);

    internal byte[] Execute(ReadOnlyMemory<byte> request, CancellationToken cancellationToken, Func<string, string, string>? readCompiled, Func<string, string>? readSource = null)
        => ExecuteCore(request, cancellationToken, readCompiled, null, readSource, false);

    internal byte[] Discover(ReadOnlyMemory<byte> request, CancellationToken cancellationToken,
        Func<string, string> list, Func<string, string> read) => ExecuteCore(request, cancellationToken, null, list, read, true);

    private byte[] ExecuteCore(ReadOnlyMemory<byte> request, CancellationToken cancellationToken, Func<string, string, string>? readCompiled,
        Func<string, string>? list, Func<string, string>? read, bool discover)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Length is < 1 or > 2097152) throw new IOException("task_script_input_size_invalid");
        var engine = new Engine(options => options
            .Strict()
            .LimitMemory(!discover && read is not null ? 128 * 1024 * 1024 : 32 * 1024 * 1024)
            .MaxStatements(1_000_000)
            .LimitRecursion(128)
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .CancellationToken(cancellationToken));
        engine.SetValue("__nexusInputJson", Utf8.GetString(request.Span));
        if (readCompiled is not null)
        {
            engine.SetValue("nexusReadCompiled", readCompiled);
            engine.Execute("Object.defineProperty(globalThis, 'nexusReadCompiled', { writable: false, configurable: false });");
        }
        if (list is not null && read is not null)
        {
            engine.SetValue("nexusListSource", list); engine.SetValue("nexusReadSource", read);
            engine.Execute("for (const name of ['nexusListSource', 'nexusReadSource']) Object.defineProperty(globalThis, name, { writable: false, configurable: false });");
        }
        if (!discover && read is not null)
        {
            engine.SetValue("nexusReadSource", read);
            engine.Execute("Object.defineProperty(globalThis, 'nexusReadSource', { writable: false, configurable: false });");
        }
        string result;
        try
        {
            engine.Execute("const __nexusInput = JSON.parse(__nexusInputJson); delete globalThis.__nexusInputJson;");
            engine.Execute(_source);
            result = engine.Evaluate(!discover ? "JSON.stringify(globalThis.nexusTaskAdapter(__nexusInput))"
                : "JSON.stringify(globalThis.nexusDiscoverScript(__nexusInput))").AsString();
        }
        catch (TimeoutException error) { throw new IOException("task_script_timeout", error); }
        var bytes = Utf8.GetBytes(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length is < 1 or > 2097152) throw new IOException("task_script_output_size_invalid");
        return bytes;
    }
}
