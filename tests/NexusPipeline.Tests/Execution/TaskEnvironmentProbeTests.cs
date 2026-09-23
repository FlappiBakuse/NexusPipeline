using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Execution.Judgement;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class TaskEnvironmentProbeTests
{
    [Theory]
    [InlineData("{}", "present", true)]
    [InlineData("{\"ip\":\"127.0.0.1\",\"port\":5555}", "present", true)]
    [InlineData("{\"ip\":\"127.0.0.1\",\"port\":\"5555\"}", "present", true)]
    [InlineData("{\"ip\":\"127.0.0.1\",\"port\":5556}", "present", false)]
    [InlineData("{\"ip\":\"127.0.0.1\",\"port\":65536}", "wrong_kind", false)]
    [InlineData("{\"ip\":null}", "missing", null)]
    [InlineData("{\"port\":null}", "missing", null)]
    [InlineData("{\"port\":true}", "unsupported", null)]
    public void UniqueMainConfigUsesDeclaredDefaultsOnlyForAbsentProperties(string json, string status, bool? matches)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-main-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "renamed-user.json");
            File.WriteAllText(file, json);
            var view = new TaskConfigView();
            view.AddConfig("config:renamed-user.json", file, "json");
            var check = new TaskEnvironmentCheckDescriptor("adb", "mainConfig", null, new JsonArray("ip"), null,
                "adb_endpoint", "none", false, false, "adb_endpoint_with_port", new JsonArray("port"), "127.0.0.1", "5555");
            var context = TaskExecutionContext.Unknown("u", "s", "preview") with { GameTarget = new("adb_endpoint", "127.0.0.1:5555", null) };
            var probe = new TaskEnvironmentProbe([check], view, context, root, "");
            var result = probe.Inspect("adb");
            Assert.Equal(status, result.Status);
            Assert.Equal(matches, result.MatchesContext);
            // Extra configuration must not become a fallback main account.
            view.AddResource("extra", file, "json");
            Assert.Equal(result, probe.Inspect("adb"));
            view.AddConfig("config:another-user.json", file, "json");
            Assert.Equal("main_config_not_unique", probe.Inspect("adb").Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UndeclaredAndSpecialTargetsDoNotBecomeFilesystemQueries()
    {
        var check = new TaskEnvironmentCheckDescriptor(
            "game-target", "host", null, null, "gameTarget", "file", "none", false, false);
        var context = TaskExecutionContext.Unknown("user", "script", "preview") with
        {
            GameTarget = new("executable", "\\\\server\\share\\game.exe", null),
        };
        var probe = new TaskEnvironmentProbe([check], new TaskConfigView(), context, "", "");

        TaskEnvironmentInspection special = probe.Inspect("game-target");
        TaskEnvironmentInspection undeclared = probe.Inspect("not-declared");

        Assert.Equal("unsupported", special.Status);
        Assert.Equal("unsupported_target", special.Reason);
        Assert.Equal("not_checked", undeclared.Status);
        Assert.Equal("undeclared_inspection", undeclared.Reason);
    }

    [Fact]
    public void DeclaredConfigSelectorReturnsOnlyTargetMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "game.exe");
            File.WriteAllText(target, "fixture");
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, JsonSerializerOptionsJson(new { gamePath = target }));
            var view = new TaskConfigView();
            view.AddConfig("config:config.json", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "none", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview");
            var probe = new TaskEnvironmentProbe([check], view, context, root, "");

            TaskEnvironmentInspection result = probe.Inspect("declared-target");

            Assert.Equal("present", result.Status);
            Assert.Equal("file", result.ActualKind);
            Assert.Equal("target_present", result.Reason);
               Assert.DoesNotContain(target, result.Reason ?? string.Empty);
            Assert.Equal("not_checked", probe.Inspect("config:other").Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeclaredConfigTargetMatchesBoundExecutableWhenContextIsKnown()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-match-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "game.exe");
            File.WriteAllText(target, "fixture");
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, JsonSerializerOptionsJson(new { gamePath = target }));
            var view = new TaskConfigView();
            view.AddConfig("config:config.json", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "none", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("executable", target, null),
            };
            var probe = new TaskEnvironmentProbe([check], view, context, root, "");

            TaskEnvironmentInspection result = probe.Inspect("declared-target");

            Assert.Equal("present", result.Status);
            Assert.Equal("file", result.ActualKind);
            Assert.True(result.MatchesContext);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WrongKindIsReportedWithoutTreatingDirectoryAsFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-kind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var check = new TaskEnvironmentCheckDescriptor(
                "game-target", "host", null, null, "gameTarget", "file", "none", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("executable", root, null),
            };
            var probe = new TaskEnvironmentProbe([check], new TaskConfigView(), context, root, "");

            TaskEnvironmentInspection result = probe.Inspect("game-target");

            Assert.Equal("wrong_kind", result.Status);
            Assert.Equal("directory", result.ActualKind);
               Assert.True(result.MatchesContext == true);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RelativeConfigTargetCannotEscapeTheDeclaredConfigDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-relative-" + Guid.NewGuid().ToString("N"));
        string configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        try
        {
            string config = Path.Combine(configDirectory, "config.json");
            File.WriteAllText(config, "{\"gamePath\":\"..\\\\outside.exe\"}");
            var view = new TaskConfigView();
            view.AddConfig("config:config.json", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "config_directory", false, false);
            var probe = new TaskEnvironmentProbe([check], view,
                TaskExecutionContext.Unknown("user", "script", "preview"), root, "");

            TaskEnvironmentInspection result = probe.Inspect("declared-target");

            Assert.Equal("not_checked", result.Status);
            Assert.Equal("target_not_resolvable", result.Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DirectoryInstallPathMatchesTheBoundExecutableParent()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-install-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "Genshin");
        Directory.CreateDirectory(install);
        try
        {
            string executable = Path.Combine(install, "YuanShen.exe");
            File.WriteAllText(executable, "fixture");
            string config = Path.Combine(root, "software.json");
            File.WriteAllText(config, JsonSerializerOptionsJson(new { installPath = install }));
            var view = new TaskConfigView();
            view.AddResource("extra-user-config", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "bettergi-game-target", "resource", "extra-user-config",
                JsonNode.Parse("[\"installPath\"]")!.AsArray(), null, "file_or_directory", "none", false, false,
                "path_or_executable_parent");
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("executable", executable, null),
            };

            TaskEnvironmentInspection result = new TaskEnvironmentProbe([check], view, context, root, "").Inspect(check.Id);

            Assert.Equal("present", result.Status);
            Assert.Equal("directory", result.ActualKind);
            Assert.True(result.MatchesContext);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AdbTargetCombinesDeclaredAddressAndPortWithoutNetworkAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-adb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "software.json");
            File.WriteAllText(config, "{\"TARGET_IP_PATH\":\"127.0.0.1\",\"TARGET_PORT\":\"16384\"}");
            var view = new TaskConfigView();
            view.AddResource("software-config", config, "json");
            var check = new TaskEnvironmentCheckDescriptor(
                "baah-adb-target", "resource", "software-config",
                JsonNode.Parse("[\"TARGET_IP_PATH\"]")!.AsArray(), null, "adb_endpoint", "none", false, false,
                "adb_endpoint_with_port", JsonNode.Parse("[\"TARGET_PORT\"]")!.AsArray());
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("adb_endpoint", "127.0.0.1:16384", null),
            };

            TaskEnvironmentInspection result = new TaskEnvironmentProbe([check], view, context, root, "").Inspect(check.Id);

            Assert.Equal("present", result.Status);
            Assert.Equal("adb_endpoint", result.ActualKind);
            Assert.True(result.MatchesContext);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RelativeTargetUsesLogicalConfigDirectoryWhenSnapshotIsRelocated()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-environment-probe-logical-" + Guid.NewGuid().ToString("N"));
        string logicalConfig = Path.Combine(root, "config");
        string physicalStore = Path.Combine(root, "store");
        Directory.CreateDirectory(logicalConfig);
        Directory.CreateDirectory(physicalStore);
        try
        {
            string logicalGame = Path.Combine(logicalConfig, "game.exe");
            File.WriteAllText(logicalGame, "fixture");
            string physicalConfig = Path.Combine(physicalStore, "config.json");
            File.WriteAllText(physicalConfig, "{\"gamePath\":\"game.exe\"}");

            TaskConfigView view = TaskConfigViewFactory.Capture(
                physicalStore,
                root,
                [],
                [],
                logicalConfig,
                []);
            var check = new TaskEnvironmentCheckDescriptor(
                "declared-target", "config", "config:config.json",
                JsonNode.Parse("[\"gamePath\"]")!.AsArray(), null, "file", "config_directory", false, false);
            var context = TaskExecutionContext.Unknown("user", "script", "preview") with
            {
                GameTarget = new("executable", logicalGame, null),
            };

            TaskEnvironmentInspection result = new TaskEnvironmentProbe([check], view, context, root, "").Inspect(check.Id);

            Assert.Equal("present", result.Status);
            Assert.True(result.MatchesContext);
        }
        finally { Directory.Delete(root, true); }
    }


    [Theory]
    [InlineData(@"C:")]
    [InlineData(@"C:relative.exe")]
    [InlineData(@"\root-relative.exe")]
    [InlineData(@"/root-relative.exe")]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData(@"\\?\C:\game.exe")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\%UNKNOWN_TARGET%\game.exe")]
    [InlineData(@"C:\game.exe:alternate")]
    public void AmbiguousOrNetworkPathsAreRejectedBeforeInspection(string target)
    {
        TaskEnvironmentInspection result = CreateHostProbe(target).Inspect("target");
        Assert.Equal("unsupported", result.Status);
    }

    [Fact]
    public void TargetDeletionAndReplacementAreRecheckedWithoutConfigChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-probe-recheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "game.exe");
            File.WriteAllText(target, "inert fixture");
            var probe = CreateHostProbe(target);
            Assert.Equal("present", probe.Inspect("target").Status);
            File.Delete(target);
            Assert.Equal("missing", probe.Inspect("target").Status);
            Directory.CreateDirectory(target);
            Assert.Equal("wrong_kind", probe.Inspect("target").Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RealDeniedAttributesRemainAccessDeniedAndRecoverAfterAclRestore()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-probe-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "game.exe");
        File.WriteAllText(target, "inert fixture");
        var file = new FileInfo(target);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(file);
        var directory = new DirectoryInfo(root);
        var originalDirectory = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        try
        {
            var deniedDirectory = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
            deniedDirectory.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, deniedDirectory);
            var denied = System.IO.FileSystemAclExtensions.GetAccessControl(file);
            denied.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ReadAttributes,
                System.Security.AccessControl.AccessControlType.Deny));
            System.IO.FileSystemAclExtensions.SetAccessControl(file, denied);
            Assert.Equal("access_denied", CreateHostProbe(target).Inspect("target").Status);
        }
        finally
        {
            var restoreDirectory = new System.Security.AccessControl.DirectorySecurity();
            restoreDirectory.SetSecurityDescriptorSddlForm(originalDirectory.GetSecurityDescriptorSddlForm(
                System.Security.AccessControl.AccessControlSections.Access), System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(directory, restoreDirectory);
            var restoreFile = new System.Security.AccessControl.FileSecurity();
            restoreFile.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(
                System.Security.AccessControl.AccessControlSections.Access), System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(file, restoreFile);
            Assert.Equal("present", CreateHostProbe(target).Inspect("target").Status);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParentJunctionIsRejectedBeforeInspectingItsDeniedDescendant()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-probe-junction-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, "destination");
        string junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(destination);
        string target = Path.Combine(destination, "game.exe");
        File.WriteAllText(target, "inert fixture");
        var file = new FileInfo(target);
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(file);
        try
        {
            // A directory junction needs no symlink privilege or UAC. Both
            // arguments are fresh GUID paths owned exclusively by this test.
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "/c", "mklink", "/J", junction, destination }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
            var denied = System.IO.FileSystemAclExtensions.GetAccessControl(file);
            denied.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ReadAttributes,
                System.Security.AccessControl.AccessControlType.Deny));
            System.IO.FileSystemAclExtensions.SetAccessControl(file, denied);
            TaskEnvironmentInspection result = CreateHostProbe(Path.Combine(junction, "game.exe")).Inspect("target");
            Assert.Equal("unsupported", result.Status);
            Assert.Equal("reparse_target", result.Reason);
        }
        finally
        {
            var restoreFile = new System.Security.AccessControl.FileSecurity();
            restoreFile.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(
                System.Security.AccessControl.AccessControlSections.Access), System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(file, restoreFile);
            if (Directory.Exists(junction)) Directory.Delete(junction); // unlink only
            Directory.Delete(root, true);
        }
    }

    private static TaskEnvironmentProbe CreateHostProbe(string target)
    {
        var check = new TaskEnvironmentCheckDescriptor("target", "host", null, null,
            "gameTarget", "file", "none", false, false);
        var context = TaskExecutionContext.Unknown("user", "script", "preview") with
        { GameTarget = new("executable", target, null) };
        return new TaskEnvironmentProbe([check], new TaskConfigView(), context, "", "");
    }

    private static string JsonSerializerOptionsJson(object value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
