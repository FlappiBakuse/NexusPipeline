using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HookInterpreterTests
{
    [Theory(DisplayName = "Actual Frozen Interpreters Preserve Supported Arguments Working Directory Pipes And Stopped Descendants")]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".com")]
    [InlineData(".ps1")]
    public async Task RunsFrozenInterpreters(string extension)
    {
        using var fixture = new InterpretedHooksFixture(extension, configuration: false, accept: false);
        var source = fixture.Source; var scope = new NativeScope(source.Instance, fixture.FrozenPre);
        using var writer = new ConfigurationWriterProcess(source.Instance, source.Lease, scope, deferRegistration: true);
        using var output = new OwnedProcessOutput();
        var capture = new ProcessOutputCapture(output);
        var lines = new List<ProcessOutputLine>();
        var reading = ReadLines();
        writer.RegisterPreparedJob(); var root = writer.Start(output);
        Exception? primary = null;
        try
        {
            var exit = await writer.AwaitRootAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, exit); Assert.True(scope.Job!.GetAccounting().ActiveProcesses >= 1);
            var stopped = await writer.StopAsync(TimeSpan.FromSeconds(20));
            Assert.True((await capture.DrainAsync(TimeSpan.FromSeconds(10))).IsComplete); await reading;
            Assert.Equal(root.ProcessId, stopped.Closure.Root!.ProcessId);
            Assert.Equal(fixture.FrozenPre.Command.Path, stopped.Closure.Root.ImagePath, ignoreCase: true);
            Assert.Contains(lines, line => line.Source == ProcessOutputSource.StandardOutput && line.Text == "hook-stdout");
            Assert.Contains(lines, line => line.Source == ProcessOutputSource.StandardError && line.Text == "hook-stderr");
            using var record = JsonDocument.Parse(File.ReadAllBytes(fixture.Report("pre")));
            var helper = record.RootElement;
            Assert.Equal(fixture.WorkingDirectory, helper.GetProperty("workingDirectory").GetString());
            Assert.Contains(stopped.Closure.Family, member => member.ProcessId == helper.GetProperty("processId").GetInt32());
            Assert.Contains(stopped.Closure.Family, member => member.ProcessId == helper.GetProperty("childProcessId").GetInt32());
            Assert.All(stopped.Closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
            Assert.Equal(stopped.Closure.Family.Length, stopped.Closure.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
            using var arguments = extension == ".ps1" ? JsonDocument.Parse(File.ReadAllBytes(fixture.Report("pre") + ".script")) : null;
            var observed = (arguments?.RootElement ?? helper).GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.Equal(fixture.Values, observed);
            if (arguments is not null) Assert.Equal(root.ProcessId, arguments.RootElement.GetProperty("processId").GetInt32());
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            try { await writer.StopAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception error) { cleanup = error; writer.Dispose(); output.Dispose(); }
            var drain = await capture.DrainAsync(TimeSpan.FromSeconds(10));
            if (drain.DeadlineExceeded) output.Dispose();
            try { await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await reading.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) { cleanup = cleanup is null ? error : new AggregateException(cleanup, error); }
            File.WriteAllBytes(fixture.Report("pre") + ".captured-output.json", JsonSerializer.SerializeToUtf8Bytes(new { extension, lines }));
            if (cleanup is not null) throw primary is null ? cleanup : new AggregateException(primary, cleanup);
        }
        async Task ReadLines()
        {
            await foreach (var line in capture.Lines.ReadAllAsync()) lines.Add(line);
        }
    }

    [Theory(DisplayName = "Inputs That Have No Supported Literal Transport Are Rejected Before Any Job Or Operation Exists")]
    [InlineData(".cmd", "percent%PATH%")]
    [InlineData(".bat", "embedded\"quote")]
    [InlineData(".com", "embedded\"quote")]
    [InlineData(".com", "percent%PATH%")]
    [InlineData(".ps1", "")]
    [InlineData(".ps1", "embedded\"quote")]
    public void RejectsUnsupportedTransport(string extension, string value)
    {
        using var fixture = new InterpretedHooksFixture(extension, configuration: false, accept: false);
        var source = fixture.Source; var catalog = source.Definitions.Read();
        var binding = catalog.Bindings.Single(binding => binding.Id == source.BindingA) with
            { PreHook = fixture.Binding.PreHook! with { Arguments = [value] } };
        var before = source.Snapshot();
        Assert.Throws<IOException>(() => ExecutionHookInputs.Read(binding, fixture.WorkingDirectory, fixture.FrozenPre.WorkingDirectoryIdentity,
            catalog.Roots.ToDictionary(root => root.Id)));
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }

    [Theory(DisplayName = "Command Interpreter Length Is Measured On The Actual Unescaped Command Line")]
    [InlineData(8191)]
    [InlineData(8192)]
    public void BoundsUnescapedCommands(int size)
    {
        var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var prefix = WindowsCommandLine.Quote(command) + " /d /s /v:off /c ";
        var tail = "\"\"" + new string('a', size - prefix.Length - 4) + "\"\"";
        string[] arguments = ["/d", "/s", "/v:off", "/c", tail];
        if (size == 8191) Assert.Equal(size, WindowsCommandLine.Build(command, arguments, CommandLineSyntax.CommandInterpreter).Length);
        else Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build(command, arguments, CommandLineSyntax.CommandInterpreter));
    }

    // This scope tests the actual native owner boundary; it is not an SQL/whole-attempt acceptance fixture.
    private sealed class NativeScope(RuntimeInstance instance, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlanHook hook) : IConfigurationWriterScope
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public Guid ProcessScopeId { get; } = Guid.NewGuid();
        public ImmutableArray<Guid> JournalIds { get; } = [Guid.NewGuid()];
        public ConfigurationEditorCommand Command { get; } = new(hook.Command.Path, hook.Command.Sha256, hook.Arguments.ToImmutableArray(), hook.WorkingDirectory);
        public CommandLineSyntax CommandLineSyntax => hook.LaunchKind == "command" ? CommandLineSyntax.CommandInterpreter : CommandLineSyntax.ArgumentVector;
        internal OwnedWorkerJob? Job { get; private set; }
        private ProcessIdentity? _root;
        private long? _elapsed;
        public void RequirePrepared() { if (_root is not null || _elapsed.HasValue) throw new IOException("fixture_native_admission_closed"); }
        public void RequireStart() { RequirePrepared(); if (Job is null) throw new IOException("fixture_native_job_missing"); }
        public void RegisterJob(OwnedWorkerJob job) { RequirePrepared(); if (Job is not null && !ReferenceEquals(Job, job)) throw new IOException("fixture_native_job_changed"); Job = job; }
        public void RecordRoot(string job, ProcessIdentity root) { RequireStart(); if (job != Job!.Name) throw new IOException("fixture_native_root_unbound"); _root = root; }
        public void CloseAdmission(long elapsedMilliseconds) { if (_elapsed is not null && _elapsed != elapsedMilliseconds) throw new IOException("fixture_native_elapsed_changed"); _elapsed = elapsedMilliseconds; }
        public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds) =>
            _elapsed == elapsedMilliseconds && closure.Root == _root ? closure with { RunElapsedMilliseconds = elapsedMilliseconds } : throw new IOException("fixture_native_closure_unbound");
        public BlobReference RecordClosed(FrozenJobClosure closure) => new JobClosureCodec(instance, new ContractReader(), new(instance, new())).Write(closure);
    }
}
