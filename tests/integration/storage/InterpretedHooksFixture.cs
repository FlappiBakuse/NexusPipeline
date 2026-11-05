using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InterpretedHooksFixture : IDisposable
{
    internal ExecutionHooksFixture Base { get; }
    internal PlanBuilderFixture Source => Base.Source;
    internal string Extension { get; }
    internal string WorkingDirectory { get; }
    internal string[] Values { get; }
    internal AutomationBinding Binding { get; }
    internal ExecutionPlanHook FrozenPre { get; }
    internal string Report(string role) => Path.Combine(Source.Root, "interpreter-" + role + ".json");
    internal ExecutionRuntimeConsumer Consumer => Base.Consumer;
    internal AttemptAcceptanceResult Accepted { get; }

    internal InterpretedHooksFixture(string extension, bool configuration = true, bool accept = true)
    {
        Base = new(configuration, accept: false); Extension = extension;
        try
        {
            var catalog = Source.Definitions.Read();
            var hookDirectory = Path.Combine(Source.Root, "hook scope & (目录)!");
            WorkingDirectory = Path.Combine(Source.Root, "working directory & (目录)!");
            Directory.CreateDirectory(hookDirectory); Directory.CreateDirectory(WorkingDirectory);
            var hookRoot = new AutomationRoot(Guid.NewGuid(), 0, "Interpreter hooks", AutomationRootKind.Script, hookDirectory);
            var cwdRoot = new AutomationRoot(Guid.NewGuid(), 0, "Hook working directory", AutomationRootKind.Script, WorkingDirectory);
            var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            var mainName = Path.GetFileName(native);
            File.Copy(native, Path.Combine(WorkingDirectory, mainName));
            foreach (var asset in JudgementInterpreterLease.Inspect(native).RuntimeFiles)
            {
                var target = Path.Combine(WorkingDirectory, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(Path.GetDirectoryName(native)!, asset.RelativePath), target);
            }
            Values = extension == ".ps1" ? ["space value", "目录&()!^|<>", "$env:UNEXPANDED; & literal", "C:\\literal\\"] :
                ["", "space value", "目录&()!^|<>", "C:\\literal\\", "@literal"];
            AutomationHook Hook(string role)
            {
                var file = extension == ".com" ? Path.GetFileNameWithoutExtension(native) + ".com" : role + " & (hook)!" + extension;
                var output = Path.Combine(hookDirectory, file);
                var launch = new[] { "execution-hook-observe", Report(role), role, Base.Marker,
                    configuration ? Source.Configuration : "-", extension == ".ps1" ? "powershell" : extension == ".com" ? "argv" : "batch" };
                if (extension == ".com")
                {
                    if (!File.Exists(output))
                    {
                        File.Copy(native, output);
                        foreach (var asset in JudgementInterpreterLease.Inspect(native).RuntimeFiles)
                        {
                            var target = Path.Combine(hookDirectory, asset.RelativePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(Path.Combine(Path.GetDirectoryName(native)!, asset.RelativePath), target);
                        }
                    }
                    return new(new(hookRoot.Id, file), launch.Concat(Values).ToImmutableArray());
                }
                var command = string.Join(" ", new[] { native }.Concat(launch).Select(NexusPipeline.Platform.Processes.WindowsCommandLine.Quote));
                string text;
                if (extension == ".ps1")
                {
                    var scriptReport = Report(role) + ".script";
                    text = "$ErrorActionPreference='Stop'\n" +
                        "[IO.File]::WriteAllText(" + Single(scriptReport) + ", (@{arguments=@($args);processId=$PID;workingDirectory=[Environment]::CurrentDirectory;requestedProcessExecutionPolicy=$env:PSExecutionPolicyPreference}|ConvertTo-Json -Compress -Depth 4))\n" +
                        "$p=New-Object Diagnostics.ProcessStartInfo\n$p.UseShellExecute=$false\n$p.CreateNoWindow=$true\n$p.RedirectStandardOutput=$true\n$p.RedirectStandardError=$true\n$p.FileName=" + Single(native) +
                        "\n$p.Arguments=" + Single(string.Join(" ", launch.Select(NexusPipeline.Platform.Processes.WindowsCommandLine.Quote))) +
                        "\n$p.WorkingDirectory=[Environment]::CurrentDirectory\n$c=[Diagnostics.Process]::Start($p)\n" +
                        "$stdout=$c.StandardOutput.ReadToEndAsync()\n$stderr=$c.StandardError.ReadToEndAsync()\n$c.WaitForExit()\n" +
                        "[Console]::Out.Write($stdout.Result)\n[Console]::Error.Write($stderr.Result)\nexit $c.ExitCode\n";
                }
                else
                {
                    if (command.Any(character => character > 127)) throw new IOException("fixture_batch_source_requires_ascii_native_paths");
                    text = "@echo off\r\nsetlocal DisableDelayedExpansion\r\nset \"NEXUS_HOOK_ARGUMENT_COUNT=" + Values.Length + "\"\r\n" +
                        string.Concat(Enumerable.Range(0, Values.Length).Select(index =>
                            "set \"NEXUS_HOOK_ARGUMENT_" + index + "=%~" + (index + 1) + "\"\r\n")) +
                        command + "\r\nexit /b %errorlevel%\r\n";
                }
                File.WriteAllText(output, text, new UTF8Encoding(extension == ".ps1"));
                return new(new(hookRoot.Id, file), Values.ToImmutableArray());
            }
            Binding = catalog.Bindings.Single(binding => binding.Id == Source.BindingA) with { PreHook = Hook("pre"), PostHook = Hook("post") };
            var script = catalog.Scripts.Single(script => script.Id == Source.ScriptId) with { RootId = cwdRoot.Id, Executable = new(cwdRoot.Id, mainName) };
            var change = new AutomationChanges(catalog.Revision, [hookRoot, cwdRoot], [script], [], [Binding], [], [], []);
            Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), ExecutionHooksFixture.Subject, change), change, DateTimeOffset.UtcNow);
            var current = Source.Definitions.Read();
            FrozenPre = ExecutionHookInputs.Read(current.Bindings.Single(binding => binding.Id == Binding.Id), WorkingDirectory,
                new() { VolumeSerial = PhysicalObjectIdentity.Read(WorkingDirectory).VolumeSerial, FileId = PhysicalObjectIdentity.Read(WorkingDirectory).FileId },
                current.Roots.ToDictionary(root => root.Id))!.Single(hook => hook.Role == "preHook");
            if (accept) Accepted = Base.Base.Service.Start(Guid.NewGuid(), ExecutionHooksFixture.Subject,
                Source.Request(new BindingTarget(Source.BindingA)), DateTimeOffset.UtcNow);
            else Accepted = null!;
        }
        catch { Base.Dispose(); throw; }
    }

    internal void Cancel()
    {
        var state = AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId);
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), ExecutionHooksFixture.Subject, Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    private static string Single(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    public void Dispose() => Base.Dispose();
}
