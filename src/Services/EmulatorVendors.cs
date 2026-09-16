using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal enum EmulatorVendor
{
    LdPlayer,
    Nox,
    BlueStacks,
}

internal enum VendorProbeState
{
    Match,
    NotApplicable,
    Error,
}

internal sealed record VendorProbeResult(
    VendorProbeState State,
    EmulatorTarget? Target = null,
    string? Error = null)
{
    internal static VendorProbeResult Match(EmulatorTarget target) => new(VendorProbeState.Match, target);

    internal static VendorProbeResult NotApplicable() => new(VendorProbeState.NotApplicable);

    internal static VendorProbeResult ErrorResult(string message) => new(VendorProbeState.Error, Error: message);
}

internal sealed record EmulatorVendorPaths(
    string ControlPath,
    string? InstallRoot,
    string? AdbPath,
    string? ConfigPath,
    string? ConfigRoot);

internal sealed record LdPlayerInstance(string Index, string Name, int? ProcessId);

internal sealed record NoxInstance(string Index, string Name, int? ProcessId);

internal sealed record BlueStacksInstance(string Id, int AdbPort);

internal static class EmulatorVendorSupport
{
    private static readonly Regex ProcessIdPattern = new(
        @"(?:pid|process[_ -]?id)\s*[:=]\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoxListLinePattern = new(
        @"^\s*(?<index>\d+)\s*(?:[,|:\t]+|\s{2,})(?<name>[^,|:\t]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoxVboxPortPattern = new(
        @"(?:hostport|host\s*port|adb[_ -]?port|forward[^\r\n]{0,20}port)\D{0,24}(?<port>\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlueStacksFlatConfigPattern = new(
        "[\\\"'](?:bst\\.)?instance\\.(?<id>[^\\\"']+?)\\.(?:adb[_\\.]?port|adbPort)[\\\"']\\s*:\\s*[\\\"']?(?<port>\\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlueStacksNestedConfigPattern = new(
        "[\\\"'](?<id>[^\\\"']+)[\\\"']\\s*:\\s*\\{[^}]{0,2000}?[\\\"'](?:adb[_\\.]?port|adbPort)[\\\"']\\s*:\\s*[\\\"']?(?<port>\\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    internal static IReadOnlyList<int> CandidateLdAdbPorts(string index)
    {
        if (!int.TryParse(index.Trim(), out int numericIndex) || numericIndex < 0)
        {
            return Array.Empty<int>();
        }
        long basePort = 5554L + numericIndex * 2L;
        return basePort is < 1 or > 65534
            ? Array.Empty<int>()
            : new[] { (int)basePort, (int)basePort + 1 };
    }

    internal static IReadOnlyList<LdPlayerInstance> ParseLdList2(string output)
    {
        var instances = new List<LdPlayerInstance>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim().Trim('\r');
            if (line.Length == 0) continue;
            string[] fields = line.Split(new[] { ',', '\t', '|' }, StringSplitOptions.TrimEntries);
            string index;
            string name;
            if (fields.Length >= 2 && int.TryParse(fields[0], out _))
            {
                index = fields[0];
                name = fields[1];
            }
            else
            {
                Match match = Regex.Match(line, @"^(?<index>\d+)\s+(?<name>\S+)", RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                index = match.Groups["index"].Value;
                name = match.Groups["name"].Value;
            }
            int? processId = ParseProcessId(line);
            instances.Add(new LdPlayerInstance(index.Trim(), name.Trim(), processId));
        }
        return instances;
    }

    internal static IReadOnlyList<NoxInstance> ParseNoxConsoleList(string output)
    {
        var instances = new List<NoxInstance>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim().Trim('\r');
            if (line.Length == 0) continue;
            Match match = NoxListLinePattern.Match(line);
            if (!match.Success)
            {
                match = Regex.Match(line, @"(?:index\s*[:=]\s*)(?<index>\d+).*?(?:name\s*[:=]\s*)?(?<name>[A-Za-z0-9_. -]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            if (!match.Success) continue;
            instances.Add(new NoxInstance(
                match.Groups["index"].Value.Trim(),
                match.Groups["name"].Value.Trim(),
                ParseProcessId(line)));
        }
        return instances;
    }

    internal static IReadOnlyList<int> ParseNoxVboxAdbPorts(string vboxContent)
    {
        var ports = new HashSet<int>();
        foreach (Match match in NoxVboxPortPattern.Matches(vboxContent))
        {
            if (int.TryParse(match.Groups["port"].Value, out int port) && port is >= 1 and <= 65535)
            {
                ports.Add(port);
            }
        }
        return ports.OrderBy(port => port).ToArray();
    }

    internal static IReadOnlyList<BlueStacksInstance> ParseBlueStacksConfig(string config)
    {
        var instances = new Dictionary<string, BlueStacksInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in BlueStacksFlatConfigPattern.Matches(config))
        {
            AddBlueStacksInstance(instances, match.Groups["id"].Value, match.Groups["port"].Value);
        }
        foreach (Match match in BlueStacksNestedConfigPattern.Matches(config))
        {
            AddBlueStacksInstance(instances, match.Groups["id"].Value, match.Groups["port"].Value);
        }
        return instances.Values.OrderBy(instance => instance.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? ResolveVendorAdb(EmulatorVendor vendor, EmulatorVendorPaths paths)
    {
        string? testAdb = TestHooks.AdbExe;
        if (!string.IsNullOrWhiteSpace(testAdb) && File.Exists(testAdb))
        {
            return testAdb;
        }

        foreach (string root in CandidateRoots(paths))
        {
            foreach (string relative in BundledAdbCandidates(vendor))
            {
                string candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    internal static EmulatorVendorPaths? ResolvePaths(EmulatorVendor vendor)
    {
        string? control = ResolveTestHook(vendor);
        string? installRoot = null;
        if (control is null)
        {
            control = FindRunningControl(vendor);
        }
        if (control is null)
        {
            foreach (string root in RegistryInstallRoots(vendor))
            {
                string? candidate = FindControlInRoot(vendor, root);
                if (candidate is not null)
                {
                    control = candidate;
                    installRoot = root;
                    break;
                }
            }
        }
        if (control is null)
        {
            foreach (string root in ProgramFileRoots())
            {
                string? candidate = FindControlInRoot(vendor, root);
                if (candidate is not null)
                {
                    control = candidate;
                    installRoot = root;
                    break;
                }
            }
        }
        if (control is null)
        {
            return null;
        }

        installRoot ??= FindInstallRoot(control);
        string? configPath = ResolveConfigPath(vendor, installRoot, control);
        string? configRoot = ResolveConfigRoot(vendor, installRoot, control);
        EmulatorVendorPaths pathSet = new(
            control,
            installRoot,
            AdbPath: null,
            configPath,
            configRoot);
        return pathSet with { AdbPath = ResolveVendorAdb(vendor, pathSet) };
    }

    internal static IReadOnlyList<string> FindNoxVboxFiles(string? configRoot)
    {
        if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot))
        {
            return Array.Empty<string>();
        }
        try
        {
            return Directory.EnumerateFiles(configRoot, "*.vbox", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[模拟器] Nox 配置扫描失败（{configRoot}）：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    internal static bool IsInstanceIdentityMatch(string path, string index, string name)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains($"/{index}/", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(path).Equals(index, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(name) && normalized.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<bool> ConfirmAdbEndpointAsync(
        string adb,
        string endpoint,
        CancellationToken token,
        int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            adb,
            new[] { "-s", endpoint, "shell", "echo", "nexus-probe" },
            Math.Max(1, Math.Min(10, timeoutSeconds)),
            token).ConfigureAwait(false);
        return ok && !EmulatorSupport.IsOfflineProbeFailure(output);
    }

    internal static bool TryKillMappedProcess(
        int? processId,
        IReadOnlyCollection<string> expectedNames,
        string display)
    {
        return processId is int pid
            && EmulatorSupport.TryKillExactProcess(pid, expectedNames, display);
    }

    internal static async Task<EmulatorCommandResult> StartControlAsync(
        string controlPath,
        IReadOnlyList<string> args,
        CancellationToken token,
        int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            controlPath,
            args,
            timeoutSeconds,
            token).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    internal static Process? StartDetachedControl(string controlPath, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(controlPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[模拟器] 启动控制程序失败（{controlPath}）：{ex.Message}");
            return null;
        }
    }

    private static void AddBlueStacksInstance(Dictionary<string, BlueStacksInstance> instances, string id, string rawPort)
    {
        if (int.TryParse(rawPort, out int port) && port is >= 1 and <= 65535 && !string.IsNullOrWhiteSpace(id))
        {
            instances[id.Trim()] = new BlueStacksInstance(id.Trim(), port);
        }
    }

    private static int? ParseProcessId(string line)
    {
        Match match = ProcessIdPattern.Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, out int pid) && pid > 0 ? pid : null;
    }

    private static string? ResolveTestHook(EmulatorVendor vendor)
    {
        string? path = vendor switch
        {
            EmulatorVendor.LdPlayer => TestHooks.LdConsoleExe,
            EmulatorVendor.Nox => TestHooks.NoxConsoleExe,
            EmulatorVendor.BlueStacks => TestHooks.BlueStacksPlayerExe,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static string? FindRunningControl(EmulatorVendor vendor)
    {
        string[] names = vendor switch
        {
            EmulatorVendor.LdPlayer => new[] { "ldconsole.exe", "dnconsole.exe" },
            EmulatorVendor.Nox => new[] { "NoxConsole.exe" },
            EmulatorVendor.BlueStacks => new[] { "HD-Player.exe" },
            _ => Array.Empty<string>(),
        };
        foreach (string name in names)
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
                {
                    using (process)
                    {
                        try
                        {
                            string? path = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                        }
                        catch
                        {
                            // 受限权限进程跳过，继续检查其他候选。
                        }
                    }
                }
            }
            catch
            {
                // 进程枚举失败按未发现处理，后续走注册表和 Program Files。
            }
        }
        return null;
    }

    private static IEnumerable<string> RegistryInstallRoots(EmulatorVendor vendor)
    {
        var locations = new List<string>();
        string[] keywords = vendor switch
        {
            EmulatorVendor.LdPlayer => new[] { "ldplayer", "leidian" },
            EmulatorVendor.Nox => new[] { "nox" },
            EmulatorVendor.BlueStacks => new[] { "bluestacks" },
            _ => Array.Empty<string>(),
        };
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (root is null) continue;
                    foreach (string subName in root.GetSubKeyNames())
                    {
                        using RegistryKey? subKey = root.OpenSubKey(subName);
                        string display = $"{subName} {subKey?.GetValue("DisplayName")}";
                        if (!keywords.Any(keyword => display.Contains(keyword, StringComparison.OrdinalIgnoreCase))) continue;
                        string? location = subKey?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location)) locations.Add(location);
                    }
                }
                catch
                {
                    // 注册表视图可能不存在或无权限，继续尝试其他视图。
                }
            }
        }
        return locations.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ProgramFileRoots()
    {
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindControlInRoot(EmulatorVendor vendor, string root)
    {
        foreach (string relative in ControlCandidates(vendor))
        {
            string candidate = Path.Combine(root, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> ControlCandidates(EmulatorVendor vendor)
    {
        return vendor switch
        {
            EmulatorVendor.LdPlayer => new[]
            {
                @"LDPlayer\LDPlayer9\ldconsole.exe",
                @"LDPlayer\LDPlayer9\dnconsole.exe",
                @"LDPlayer9\ldconsole.exe",
                @"LDPlayer9\dnconsole.exe",
            },
            EmulatorVendor.Nox => new[]
            {
                @"Nox\bin\NoxConsole.exe",
                @"Nox\NoxConsole.exe",
            },
            EmulatorVendor.BlueStacks => new[]
            {
                @"BlueStacks_nxt\HD-Player.exe",
                @"BlueStacks\HD-Player.exe",
            },
            _ => Array.Empty<string>(),
        };
    }

    private static IEnumerable<string> BundledAdbCandidates(EmulatorVendor vendor)
    {
        return vendor switch
        {
            EmulatorVendor.LdPlayer => new[] { "adb.exe", @"LDPlayer9\adb.exe", @"LDPlayer\adb.exe" },
            EmulatorVendor.Nox => new[] { @"bin\nox_adb.exe", "nox_adb.exe" },
            EmulatorVendor.BlueStacks => new[] { "HD-Adb.exe", @"bin\HD-Adb.exe" },
            _ => Array.Empty<string>(),
        };
    }

    private static IEnumerable<string> CandidateRoots(EmulatorVendorPaths paths)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(paths.InstallRoot)) roots.Add(paths.InstallRoot);
        string? controlDir = Path.GetDirectoryName(paths.ControlPath);
        for (int i = 0; i < 3 && !string.IsNullOrWhiteSpace(controlDir); i++)
        {
            roots.Add(controlDir);
            controlDir = Path.GetDirectoryName(controlDir);
        }
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindInstallRoot(string control)
    {
        string? directory = Path.GetDirectoryName(control);
        return directory is null ? null : Directory.Exists(directory) ? directory : null;
    }

    private static string? ResolveConfigPath(EmulatorVendor vendor, string? installRoot, string control)
    {
        if (vendor == EmulatorVendor.BlueStacks)
        {
            string? hook = TestHooks.BlueStacksConfigPath;
            if (!string.IsNullOrWhiteSpace(hook) && File.Exists(hook)) return hook;
            foreach (string root in CandidateRoots(new EmulatorVendorPaths(control, installRoot, null, null, null)))
            {
                string candidate = Path.Combine(root, "bluestacks.conf");
                if (File.Exists(candidate)) return candidate;
            }
            string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlueStacks_nxt", "bluestacks.conf");
            return File.Exists(common) ? common : null;
        }
        return null;
    }

    private static string? ResolveConfigRoot(EmulatorVendor vendor, string? installRoot, string control)
    {
        if (vendor != EmulatorVendor.Nox) return null;
        foreach (string root in CandidateRoots(new EmulatorVendorPaths(control, installRoot, null, null, null)))
        {
            string candidate = Path.Combine(root, "BignoxVMS");
            if (Directory.Exists(candidate)) return candidate;
        }
        string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nox", "BignoxVMS");
        return Directory.Exists(common) ? common : null;
    }
}

internal static class EmulatorVendorDetector
{
    internal static async Task<VendorProbeResult> ProbeAsync(
        EmulatorVendor vendor,
        string endpoint,
        int port,
        CancellationToken token,
        int timeoutSeconds)
    {
        EmulatorVendorPaths? paths = EmulatorVendorSupport.ResolvePaths(vendor);
        if (paths is null)
        {
            return VendorProbeResult.NotApplicable();
        }
        return vendor switch
        {
            EmulatorVendor.LdPlayer => await ProbeLdAsync(paths, endpoint, port, token, timeoutSeconds).ConfigureAwait(false),
            EmulatorVendor.Nox => await ProbeNoxAsync(paths, endpoint, port, token, timeoutSeconds).ConfigureAwait(false),
            EmulatorVendor.BlueStacks => await ProbeBlueStacksAsync(paths, endpoint, port, token, timeoutSeconds).ConfigureAwait(false),
            _ => VendorProbeResult.NotApplicable(),
        };
    }

    private static async Task<VendorProbeResult> ProbeLdAsync(
        EmulatorVendorPaths paths,
        string endpoint,
        int port,
        CancellationToken token,
        int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            paths.ControlPath,
            new[] { "list2" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        if (!ok)
        {
            return VendorProbeResult.ErrorResult($"LDPlayer list2 失败：{output.Trim()}");
        }
        IReadOnlyList<LdPlayerInstance> instances = EmulatorVendorSupport.ParseLdList2(output);
        if (instances.Count == 0 && !string.IsNullOrWhiteSpace(output))
        {
            return VendorProbeResult.ErrorResult("LDPlayer list2 输出无法解析实例清单");
        }
        LdPlayerInstance[] matches = instances
            .Where(instance => EmulatorVendorSupport.CandidateLdAdbPorts(instance.Index).Contains(port))
            .ToArray();
        if (matches.Length == 0) return VendorProbeResult.NotApplicable();
        if (matches.Length > 1) return VendorProbeResult.ErrorResult("LDPlayer 端点同时匹配多个实例索引");
        if (paths.AdbPath is null) return VendorProbeResult.ErrorResult("已识别 LDPlayer 实例，但未找到其 bundled adb.exe");
        if (!await EmulatorVendorSupport.ConfirmAdbEndpointAsync(paths.AdbPath, endpoint, token, timeoutSeconds).ConfigureAwait(false))
        {
            return VendorProbeResult.ErrorResult("LDPlayer 实例索引与 ADB 端点的映射无法确认");
        }
        LdPlayerInstance instance = matches[0];
        return VendorProbeResult.Match(new EmulatorTarget(
            EmulatorKind.LdPlayer,
            endpoint,
            VendorControlPath: paths.ControlPath,
            VendorInstanceId: instance.Index,
            VendorAdbExecutable: paths.AdbPath,
            VendorProcessId: instance.ProcessId,
            VendorInstallRoot: paths.InstallRoot));
    }

    private static async Task<VendorProbeResult> ProbeNoxAsync(
        EmulatorVendorPaths paths,
        string endpoint,
        int port,
        CancellationToken token,
        int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            paths.ControlPath,
            new[] { "list" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        if (!ok)
        {
            return VendorProbeResult.ErrorResult($"NoxConsole list 失败：{output.Trim()}");
        }
        IReadOnlyList<NoxInstance> instances = EmulatorVendorSupport.ParseNoxConsoleList(output);
        if (instances.Count == 0 && !string.IsNullOrWhiteSpace(output))
        {
            return VendorProbeResult.ErrorResult("NoxConsole list 输出无法解析实例清单");
        }
        if (instances.Count == 0) return VendorProbeResult.NotApplicable();
        IReadOnlyList<string> files = EmulatorVendorSupport.FindNoxVboxFiles(paths.ConfigRoot);
        if (files.Count == 0)
        {
            return VendorProbeResult.ErrorResult("Nox 实例存在，但未找到可解析的 BignoxVMS .vbox 映射");
        }

        var matches = new List<(NoxInstance Instance, string File)>();
        bool sawConfig = false;
        bool sawEndpoint = false;
        foreach (string file in files)
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return VendorProbeResult.ErrorResult($"Nox 配置读取失败：{ex.Message}");
            }
            IReadOnlyList<int> ports = EmulatorVendorSupport.ParseNoxVboxAdbPorts(content);
            if (ports.Count > 0) sawConfig = true;
            if (!ports.Contains(port)) continue;
            sawEndpoint = true;
            NoxInstance[] identityMatches = instances
                .Where(instance => EmulatorVendorSupport.IsInstanceIdentityMatch(file, instance.Index, instance.Name))
                .ToArray();
            if (identityMatches.Length == 1) matches.Add((identityMatches[0], file));
            else if (identityMatches.Length > 1) return VendorProbeResult.ErrorResult("Nox 配置文件无法唯一对应实例身份");
        }
        if (matches.Count == 0)
        {
            if (!sawConfig) return VendorProbeResult.ErrorResult("Nox BignoxVMS 配置未包含可解析的 ADB 端口");
            return sawEndpoint
                ? VendorProbeResult.ErrorResult("Nox ADB 端点已出现在配置中，但无法唯一对应实例身份")
                : VendorProbeResult.NotApplicable();
        }
        if (matches.Count > 1) return VendorProbeResult.ErrorResult("Nox ADB 端点同时匹配多个实例");
        if (paths.AdbPath is null) return VendorProbeResult.ErrorResult("已识别 Nox 实例，但未找到其 bundled nox_adb.exe");
        if (!await EmulatorVendorSupport.ConfirmAdbEndpointAsync(paths.AdbPath, endpoint, token, timeoutSeconds).ConfigureAwait(false))
        {
            return VendorProbeResult.ErrorResult("Nox 实例身份与 ADB 端点的映射无法确认");
        }
        NoxInstance instance = matches[0].Instance;
        return VendorProbeResult.Match(new EmulatorTarget(
            EmulatorKind.Nox,
            endpoint,
            VendorControlPath: paths.ControlPath,
            VendorInstanceId: instance.Index,
            VendorAdbExecutable: paths.AdbPath,
            VendorProcessId: instance.ProcessId,
            VendorInstallRoot: paths.InstallRoot));
    }

    private static async Task<VendorProbeResult> ProbeBlueStacksAsync(
        EmulatorVendorPaths paths,
        string endpoint,
        int port,
        CancellationToken token,
        int timeoutSeconds)
    {
        if (paths.ConfigPath is null) return VendorProbeResult.ErrorResult("已识别 BlueStacks 控制程序，但未找到 bluestacks.conf");
        string config;
        try
        {
            config = await File.ReadAllTextAsync(paths.ConfigPath, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return VendorProbeResult.ErrorResult($"BlueStacks 配置读取失败：{ex.Message}");
        }
        IReadOnlyList<BlueStacksInstance> instances = EmulatorVendorSupport.ParseBlueStacksConfig(config);
        if (instances.Count == 0) return VendorProbeResult.ErrorResult("BlueStacks 配置未包含可解析的实例 ADB 端口");
        BlueStacksInstance[] matches = instances.Where(instance => instance.AdbPort == port).ToArray();
        if (matches.Length == 0) return VendorProbeResult.NotApplicable();
        if (matches.Length > 1) return VendorProbeResult.ErrorResult("BlueStacks ADB 端点同时匹配多个实例");
        if (paths.AdbPath is null) return VendorProbeResult.ErrorResult("已识别 BlueStacks 实例，但未找到其 bundled HD-Adb.exe");
        if (!await EmulatorVendorSupport.ConfirmAdbEndpointAsync(paths.AdbPath, endpoint, token, timeoutSeconds).ConfigureAwait(false))
        {
            return VendorProbeResult.ErrorResult("BlueStacks 实例与 ADB 端点的映射无法确认");
        }
        return VendorProbeResult.Match(new EmulatorTarget(
            EmulatorKind.BlueStacks,
            endpoint,
            VendorControlPath: paths.ControlPath,
            VendorInstanceId: matches[0].Id,
            VendorAdbExecutable: paths.AdbPath,
            VendorInstallRoot: paths.InstallRoot));
    }
}

/// <summary>各厂商共用 ADB 操作；目标已检测冻结后，生命周期内始终使用同一 bundled adb。</summary>
internal abstract class VendorAdbEmulatorDriverBase : IEmulatorDriver
{
    protected readonly EmulatorTarget Target;

    protected VendorAdbEmulatorDriverBase(EmulatorTarget target)
    {
        Target = target;
    }

    public abstract EmulatorKind Kind { get; }

    protected string? AdbExecutable => Target.VendorAdbExecutable ?? Target.AdbExecutable;

    public abstract Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds);

    public async Task<EmulatorCommandResult> StartAppAsync(IReadOnlyList<string> startArgs, CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return EmulatorCommandResult.Failure("未找到厂商 bundled adb 可执行文件");
        var shellArgs = new List<string> { "am", "start" };
        shellArgs.AddRange(startArgs);
        (bool ok, string output) = await EmulatorSupport.AdbShellAsync(
            AdbExecutable,
            Target.Endpoint,
            shellArgs.ToArray(),
            timeoutSeconds,
            token).ConfigureAwait(false);
        return ok && !EmulatorSupport.AmStartFailed(output)
            ? EmulatorCommandResult.Success(output)
            : EmulatorCommandResult.Failure(output);
    }

    public Task<string?> GetForegroundPackageAsync(CancellationToken token, int timeoutSeconds)
    {
        return string.IsNullOrWhiteSpace(AdbExecutable)
            ? Task.FromResult<string?>(null)
            : EmulatorSupport.GetForegroundPackageAsync(AdbExecutable, Target.Endpoint, token, timeoutSeconds);
    }

    public async Task<EmulatorBinaryResult> CaptureScreenAsync(CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return EmulatorBinaryResult.Failure("未找到厂商 bundled adb 可执行文件");
        EmulatorBinaryResult result = await EmulatorSupport.RunBinaryCommandAsync(
            AdbExecutable,
            new[] { "-s", Target.Endpoint, "exec-out", "screencap", "-p" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        return result.Ok && EmulatorSupport.IsPng(result.Data)
            ? result
            : EmulatorBinaryResult.Failure(result.Error.Length > 0 ? result.Error : "厂商 ADB 未返回有效 PNG 截图");
    }

    public async Task<EmulatorCommandResult> StopAppAsync(string? packageName, CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return EmulatorCommandResult.Success("未配置目标包名，跳过应用关闭");
        if (packageName is "com.android.systemui" or "com.android.launcher" or "app.lawnchair" or "com.mumu.launcher")
        {
            return EmulatorCommandResult.Success($"目标包名为系统桌面（{packageName}），跳过应用关闭");
        }
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return EmulatorCommandResult.Failure("未找到厂商 bundled adb 可执行文件");
        (bool ok, string output) = await EmulatorSupport.AdbShellAsync(
            AdbExecutable,
            Target.Endpoint,
            new[] { "am", "force-stop", packageName },
            timeoutSeconds,
            token).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    public abstract Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds);

    protected async Task<(bool Ok, string Message)> ShutdownViaAdbAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return (false, "未找到厂商 bundled adb 可执行文件");
        (bool ok, string output) = await EmulatorSupport.AdbShellAsync(
            AdbExecutable,
            Target.Endpoint,
            new[] { "reboot", "-p" },
            30,
            token).ConfigureAwait(false);
        if (!ok) return (false, $"adb shell reboot -p 失败：{output.Trim()}");
        return await EmulatorSupport.WaitEmulatorOfflineAsync(AdbExecutable, Target.Endpoint, token).ConfigureAwait(false)
            ? (true, "已通过厂商 bundled adb shell reboot -p 关闭模拟器")
            : (false, "已执行 adb shell reboot -p，但等待超时仍未确认模拟器离线");
    }

    protected static bool LooksAlreadyRunning(string output)
    {
        return output.Contains("already", StringComparison.OrdinalIgnoreCase)
            || output.Contains("running", StringComparison.OrdinalIgnoreCase)
            || output.Contains("started", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class LdPlayerEmulatorDriver : VendorAdbEmulatorDriverBase
{
    private static readonly string[] ProcessNames = { "ldconsole", "dnconsole", "dnplayer", "LdVBoxHeadless" };

    public LdPlayerEmulatorDriver(EmulatorTarget target) : base(target)
    {
        if (target.Kind != EmulatorKind.LdPlayer || string.IsNullOrWhiteSpace(target.VendorControlPath) || string.IsNullOrWhiteSpace(target.VendorInstanceId))
        {
            throw new ArgumentException("LDPlayer driver 需要绑定完整 vendor target。", nameof(target));
        }
    }

    public override EmulatorKind Kind => EmulatorKind.LdPlayer;

    public override async Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorCommandResult launch = await EmulatorVendorSupport.StartControlAsync(
            Target.VendorControlPath!,
            new[] { "launch", "--index", Target.VendorInstanceId! },
            token,
            timeoutSeconds).ConfigureAwait(false);
        if (!launch.Ok && !LooksAlreadyRunning(launch.Output)) return launch;
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return EmulatorCommandResult.Failure("未找到 LDPlayer bundled adb.exe");
        (bool ok, string output) = await EmulatorSupport.AdbConnectAsync(AdbExecutable, Target.Endpoint, token, timeoutSeconds).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    public override async Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorCommandResult quit = await EmulatorVendorSupport.StartControlAsync(
            Target.VendorControlPath!,
            new[] { "quit", "--index", Target.VendorInstanceId! },
            token,
            timeoutSeconds).ConfigureAwait(false);
        if (quit.Ok && await WaitOffline(token).ConfigureAwait(false))
        {
            return EmulatorCommandResult.Success($"已通过 LDPlayer 关闭实例 {Target.VendorInstanceId}");
        }

        (bool adbOk, string adbMessage) = await ShutdownViaAdbAsync(token).ConfigureAwait(false);
        if (adbOk) return EmulatorCommandResult.Success(adbMessage);

        if (await TryKillReconfirmedProcessAsync(token, timeoutSeconds).ConfigureAwait(false)
            && await WaitOffline(token).ConfigureAwait(false))
        {
            return EmulatorCommandResult.Success($"已按重新确认的 LDPlayer 实例 PID 关闭实例 {Target.VendorInstanceId}");
        }
        return EmulatorCommandResult.Failure($"LDPlayer 关闭未确认：quit={quit.Output.Trim()}；adb={adbMessage}");
    }

    private async Task<bool> WaitOffline(CancellationToken token)
    {
        return !string.IsNullOrWhiteSpace(AdbExecutable)
            && await EmulatorSupport.WaitEmulatorOfflineAsync(AdbExecutable, Target.Endpoint, token).ConfigureAwait(false);
    }

    private async Task<bool> TryKillReconfirmedProcessAsync(CancellationToken token, int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            Target.VendorControlPath!,
            new[] { "list2" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        if (!ok) return false;
        LdPlayerInstance[] matches = EmulatorVendorSupport.ParseLdList2(output)
            .Where(item => string.Equals(item.Index, Target.VendorInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || Target.VendorProcessId is not int expectedPid || matches[0].ProcessId != expectedPid)
        {
            return false;
        }
        return EmulatorVendorSupport.TryKillMappedProcess(matches[0].ProcessId, ProcessNames, "LDPlayer");
    }
}

internal sealed class NoxEmulatorDriver : VendorAdbEmulatorDriverBase
{
    private static readonly string[] ProcessNames = { "NoxConsole", "Nox", "NoxVMHandle" };

    public NoxEmulatorDriver(EmulatorTarget target) : base(target)
    {
        if (target.Kind != EmulatorKind.Nox || string.IsNullOrWhiteSpace(target.VendorControlPath) || string.IsNullOrWhiteSpace(target.VendorInstanceId))
        {
            throw new ArgumentException("Nox driver 需要绑定完整 vendor target。", nameof(target));
        }
    }

    public override EmulatorKind Kind => EmulatorKind.Nox;

    public override async Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorCommandResult launch = await EmulatorVendorSupport.StartControlAsync(
            Target.VendorControlPath!,
            new[] { "launch", $"-index:{Target.VendorInstanceId}" },
            token,
            timeoutSeconds).ConfigureAwait(false);
        if (!launch.Ok && !LooksAlreadyRunning(launch.Output)) return launch;
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return EmulatorCommandResult.Failure("未找到 Nox bundled nox_adb.exe");
        (bool ok, string output) = await EmulatorSupport.AdbConnectAsync(AdbExecutable, Target.Endpoint, token, timeoutSeconds).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    public override async Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds)
    {
        EmulatorCommandResult quit = await EmulatorVendorSupport.StartControlAsync(
            Target.VendorControlPath!,
            new[] { "quit", $"-index:{Target.VendorInstanceId}" },
            token,
            timeoutSeconds).ConfigureAwait(false);
        if (quit.Ok && await WaitOffline(token).ConfigureAwait(false))
        {
            return EmulatorCommandResult.Success($"已通过 Nox 关闭实例 {Target.VendorInstanceId}");
        }

        (bool adbOk, string adbMessage) = await ShutdownViaAdbAsync(token).ConfigureAwait(false);
        if (adbOk) return EmulatorCommandResult.Success(adbMessage);

        if (await TryKillReconfirmedProcessAsync(token, timeoutSeconds).ConfigureAwait(false)
            && await WaitOffline(token).ConfigureAwait(false))
        {
            return EmulatorCommandResult.Success($"已按重新确认的 Nox 实例 PID 关闭实例 {Target.VendorInstanceId}");
        }
        return EmulatorCommandResult.Failure($"Nox 关闭未确认：quit={quit.Output.Trim()}；adb={adbMessage}");
    }

    private async Task<bool> WaitOffline(CancellationToken token)
    {
        return !string.IsNullOrWhiteSpace(AdbExecutable)
            && await EmulatorSupport.WaitEmulatorOfflineAsync(AdbExecutable, Target.Endpoint, token).ConfigureAwait(false);
    }

    private async Task<bool> TryKillReconfirmedProcessAsync(CancellationToken token, int timeoutSeconds)
    {
        (bool ok, string output) = await EmulatorSupport.RunCommandAsync(
            Target.VendorControlPath!,
            new[] { "list" },
            timeoutSeconds,
            token).ConfigureAwait(false);
        if (!ok) return false;
        NoxInstance[] matches = EmulatorVendorSupport.ParseNoxConsoleList(output)
            .Where(item => string.Equals(item.Index, Target.VendorInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || Target.VendorProcessId is not int expectedPid || matches[0].ProcessId != expectedPid)
        {
            return false;
        }
        return EmulatorVendorSupport.TryKillMappedProcess(matches[0].ProcessId, ProcessNames, "Nox");
    }
}

internal sealed class BlueStacksEmulatorDriver : VendorAdbEmulatorDriverBase
{
    private static readonly string[] ProcessNames = { "HD-Player" };

    public BlueStacksEmulatorDriver(EmulatorTarget target) : base(target)
    {
        if (target.Kind != EmulatorKind.BlueStacks || string.IsNullOrWhiteSpace(target.VendorControlPath) || string.IsNullOrWhiteSpace(target.VendorInstanceId))
        {
            throw new ArgumentException("BlueStacks driver 需要绑定完整 vendor target。", nameof(target));
        }
    }

    public override EmulatorKind Kind => EmulatorKind.BlueStacks;

    public override async Task<EmulatorCommandResult> EnsureReadyAsync(CancellationToken token, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(AdbExecutable)) return EmulatorCommandResult.Failure("未找到 BlueStacks bundled HD-Adb.exe");
        if (SystemActions.IsCommandFile(Target.VendorControlPath!))
        {
            EmulatorCommandResult stubResult = await EmulatorVendorSupport.StartControlAsync(
                Target.VendorControlPath!,
                new[] { "--instance", Target.VendorInstanceId! },
                token,
                timeoutSeconds).ConfigureAwait(false);
            if (!stubResult.Ok && !LooksAlreadyRunning(stubResult.Output)) return stubResult;
        }
        else
        {
        Process? control = EmulatorVendorSupport.StartDetachedControl(
            Target.VendorControlPath!,
            new[] { "--instance", Target.VendorInstanceId! });
        if (control is null) return EmulatorCommandResult.Failure("HD-Player.exe --instance 启动失败");
        using (control)
        {
            try
            {
                await Task.Delay(TestHooks.ScaledMs(250), token).ConfigureAwait(false);
                if (control.HasExited && control.ExitCode != 0)
                {
                    return EmulatorCommandResult.Failure($"HD-Player.exe --instance 返回码 {control.ExitCode}");
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已被宿主接管并退出，若退出码不可读交给 ADB 连通性确认。
            }
        }
        }
        (bool ok, string output) = await EmulatorSupport.AdbConnectAsync(AdbExecutable, Target.Endpoint, token, timeoutSeconds).ConfigureAwait(false);
        return ok ? EmulatorCommandResult.Success(output) : EmulatorCommandResult.Failure(output);
    }

    public override async Task<EmulatorCommandResult> ShutdownAsync(CancellationToken token, int timeoutSeconds)
    {
        (bool adbOk, string adbMessage) = await ShutdownViaAdbAsync(token).ConfigureAwait(false);
        if (adbOk) return EmulatorCommandResult.Success(adbMessage);

        // BlueStacks 配置通常无法安全提供单实例 PID；没有配置证明时明确失败，绝不按名称清理全部 HD-Player。
        if (Target.VendorProcessId is int pid
            && EmulatorVendorSupport.TryKillMappedProcess(pid, ProcessNames, "BlueStacks")
            && await WaitOffline(token).ConfigureAwait(false))
        {
            return EmulatorCommandResult.Success($"已按配置重新确认的 BlueStacks 实例 PID 关闭实例 {Target.VendorInstanceId}");
        }
        return EmulatorCommandResult.Failure($"BlueStacks 关闭未确认：{adbMessage}；未取得可安全复核的单实例 PID");
    }

    private async Task<bool> WaitOffline(CancellationToken token)
    {
        return !string.IsNullOrWhiteSpace(AdbExecutable)
            && await EmulatorSupport.WaitEmulatorOfflineAsync(AdbExecutable, Target.Endpoint, token).ConfigureAwait(false);
    }
}
