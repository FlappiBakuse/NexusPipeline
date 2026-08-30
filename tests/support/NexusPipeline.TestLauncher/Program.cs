using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

const int launcherFailureExitCode = 2;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("[错误] Medium Integrity 测试启动器仅支持 Windows。");
    return launcherFailureExitCode;
}

var command = args.Length > 0 ? args : Array.Empty<string>();
if (command.Length == 0)
{
    Console.Error.WriteLine("用法：NexusPipeline.TestLauncher <命令> [参数...]");
    return launcherFailureExitCode;
}

try
{
    var ciUser = Environment.GetEnvironmentVariable("NEXUS_CI_TEST_USER");
    var ciPassword = Environment.GetEnvironmentVariable("NEXUS_CI_TEST_PASSWORD");
    if (!string.IsNullOrWhiteSpace(ciUser) || !string.IsNullOrWhiteSpace(ciPassword))
    {
        if (string.IsNullOrWhiteSpace(ciUser) || string.IsNullOrWhiteSpace(ciPassword))
        {
            throw new InvalidOperationException("NEXUS_CI_TEST_USER 与 NEXUS_CI_TEST_PASSWORD 必须同时设置。");
        }

        var ciDomain = Environment.GetEnvironmentVariable("NEXUS_CI_TEST_DOMAIN") ?? ".";
        Environment.SetEnvironmentVariable("NEXUS_CI_TEST_USER", null);
        Environment.SetEnvironmentVariable("NEXUS_CI_TEST_PASSWORD", null);
        Environment.SetEnvironmentVariable("NEXUS_CI_TEST_DOMAIN", null);
        Console.WriteLine("[TestLauncher] mode=ci-logon-user integrity=Medium restricted=false administrator-enabled=false");
        return RunWithLogon(command, ciUser, ciPassword, ciDomain);
    }

    using var mediumToken = AcquireMediumToken(out var tokenState);
    Console.WriteLine(
        $"[TestLauncher] mode={tokenState.Mode} integrity={DescribeIntegrity(tokenState.Integrity)} " +
        $"restricted={tokenState.Restricted.ToString().ToLowerInvariant()} " +
        $"administrator-enabled={tokenState.AdministratorEnabled.ToString().ToLowerInvariant()}");
    return RunWithToken(mediumToken, command);
}
catch (Exception error)
{
    Console.Error.WriteLine($"[TestLauncher] 启动测试失败：{error.Message}");
    return launcherFailureExitCode;
}

static SafeNativeHandle AcquireMediumToken(out TokenState tokenState)
{
    using var currentToken = OpenCurrentProcessToken();
    var currentIntegrity = ReadIntegrityLevel(currentToken.Handle);
    if (currentIntegrity < NativeMethods.MediumIntegrityRid)
    {
        throw new InvalidOperationException($"当前 token 低于 Medium Integrity（RID={currentIntegrity}）。");
    }

    if (IsSystemToken(currentToken.Handle))
    {
        throw new InvalidOperationException("当前 token 属于 LocalSystem，无法安全归一化为普通用户测试 token。");
    }

    if (currentIntegrity == NativeMethods.MediumIntegrityRid)
    {
        return ValidateCandidate(
            DuplicatePrimaryToken(currentToken.Handle),
            "current-token",
            restricted: false,
            out tokenState);
    }

    if (TryGetLinkedToken(currentToken.Handle, out var linkedTokenHandle))
    {
        using var linkedToken = new SafeNativeHandle(linkedTokenHandle);
        return ValidateCandidate(
            DuplicatePrimaryToken(linkedToken.Handle),
            "linked-token",
            restricted: false,
            out tokenState);
    }

    return ValidateCandidate(
        CreateRestrictedMediumToken(currentToken.Handle),
        "restricted-token",
        restricted: true,
        out tokenState);
}

static SafeNativeHandle ValidateCandidate(
    SafeNativeHandle candidate,
    string mode,
    bool restricted,
    out TokenState tokenState)
{
    try
    {
        var integrity = ReadIntegrityLevel(candidate.Handle);
        if (integrity != NativeMethods.MediumIntegrityRid)
        {
            throw new InvalidOperationException($"获得的测试 token 不是 Medium Integrity（RID={integrity}）。");
        }

        var administratorEnabled = IsSidEnabled(candidate.Handle, NativeMethods.AdministratorsSid);
        var powerUsersEnabled = IsSidEnabled(candidate.Handle, NativeMethods.PowerUsersSid);
        if (administratorEnabled || powerUsersEnabled)
        {
            throw new InvalidOperationException("测试 token 仍启用了管理员组或 Power Users 组。");
        }

        if (restricted && !HasTokenRestrictions(candidate.Handle))
        {
            throw new InvalidOperationException("受限测试 token 未经过 Windows token 筛选。");
        }

        tokenState = new TokenState(mode, integrity, restricted, administratorEnabled);
        return candidate;
    }
    catch
    {
        candidate.Dispose();
        throw;
    }
}

static int RunWithToken(SafeNativeHandle mediumToken, string[] command)
{
    var commandLine = new StringBuilder(string.Join(" ", command.Select(QuoteArgument)));
    NativeMethods.ProcessInformation processInfo;
    using (var standardHandles = DuplicateStandardHandles())
    {
        var startupInfo = new NativeMethods.StartupInfo
        {
            cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
            dwFlags = NativeMethods.StartfUseStdHandles,
            hStdInput = standardHandles.Input.Handle,
            hStdOutput = standardHandles.Output.Handle,
            hStdError = standardHandles.Error.Handle,
        };

        if (!NativeMethods.CreateProcessAsUser(
            mediumToken.Handle,
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            true,
            NativeMethods.CreateUnicodeEnvironment,
            IntPtr.Zero,
            Environment.CurrentDirectory,
            ref startupInfo,
            out processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser 失败");
        }
    }

    return WaitForProcess(processInfo);
}

static int RunWithLogon(string[] command, string userName, string password, string domain)
{
    var commandLine = new StringBuilder(string.Join(" ", command.Select(QuoteArgument)));
    NativeMethods.ProcessInformation processInfo;
    var environment = CreateInheritedUserEnvironment(userName, password, domain);
    try
    {
        using (var standardHandles = DuplicateStandardHandles())
        {
            var startupInfo = new NativeMethods.StartupInfo
            {
                cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                dwFlags = NativeMethods.StartfUseStdHandles,
                hStdInput = standardHandles.Input.Handle,
                hStdOutput = standardHandles.Output.Handle,
                hStdError = standardHandles.Error.Handle,
            };

            if (!NativeMethods.CreateProcessWithLogon(
                userName,
                domain,
                password,
                NativeMethods.LogonWithProfile,
                null,
                commandLine,
                NativeMethods.CreateUnicodeEnvironment,
                environment,
                Environment.CurrentDirectory,
                ref startupInfo,
                out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessWithLogonW 失败");
            }
        }
    }
    finally
    {
        NativeMethods.DestroyEnvironmentBlock(environment);
    }

    return WaitForProcess(processInfo);
}

static IntPtr CreateInheritedUserEnvironment(string userName, string password, string domain)
{
    if (!NativeMethods.LogonUser(
        userName,
        domain,
        password,
        NativeMethods.Logon32LogonInteractive,
        NativeMethods.Logon32ProviderDefault,
        out var tokenHandle))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "LogonUserW 失败");
    }

    using var token = new SafeNativeHandle(tokenHandle);
    if (!NativeMethods.CreateEnvironmentBlock(out var environment, token.Handle, true))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock 失败");
    }

    return environment;
}

static InheritedStandardHandles DuplicateStandardHandles()
{
    SafeNativeHandle? input = null;
    SafeNativeHandle? output = null;
    SafeNativeHandle? error = null;
    try
    {
        input = DuplicateStandardHandle(NativeMethods.StdInputHandle, "标准输入");
        output = DuplicateStandardHandle(NativeMethods.StdOutputHandle, "标准输出");
        error = DuplicateStandardHandle(NativeMethods.StdErrorHandle, "标准错误");
        return new InheritedStandardHandles(input, output, error);
    }
    catch
    {
        input?.Dispose();
        output?.Dispose();
        error?.Dispose();
        throw;
    }
}

static SafeNativeHandle DuplicateStandardHandle(int standardHandle, string label)
{
    var sourceHandle = NativeMethods.GetStdHandle(standardHandle);
    if (sourceHandle == IntPtr.Zero || sourceHandle == new IntPtr(-1))
    {
        throw new InvalidOperationException($"无法取得{label}句柄，测试未启动。");
    }

    if (!NativeMethods.DuplicateHandle(
        NativeMethods.GetCurrentProcess(),
        sourceHandle,
        NativeMethods.GetCurrentProcess(),
        out var duplicatedHandle,
        0,
        true,
        NativeMethods.DuplicateSameAccess))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"复制{label}句柄失败");
    }

    return new SafeNativeHandle(duplicatedHandle);
}

static int WaitForProcess(NativeMethods.ProcessInformation processInfo)
{
    using var processHandle = new SafeNativeHandle(processInfo.hProcess);
    using var threadHandle = new SafeNativeHandle(processInfo.hThread);
    var waitResult = NativeMethods.WaitForSingleObject(processHandle.Handle, NativeMethods.Infinite);
    if (waitResult != NativeMethods.WaitObject0)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"等待测试进程失败（结果码 {waitResult}）");
    }

    if (!NativeMethods.GetExitCodeProcess(processHandle.Handle, out var exitCode))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "读取测试进程退出码失败");
    }

    return unchecked((int)exitCode);
}

static SafeNativeHandle OpenCurrentProcessToken()
{
    if (!NativeMethods.OpenProcessToken(
        NativeMethods.GetCurrentProcess(),
        NativeMethods.TokenCreateProcessAccess,
        out var token))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken 失败");
    }

    return new SafeNativeHandle(token);
}

static SafeNativeHandle DuplicatePrimaryToken(IntPtr sourceToken)
{
    if (!NativeMethods.DuplicateTokenEx(
        sourceToken,
        NativeMethods.TokenCreateProcessAccess,
        IntPtr.Zero,
        NativeMethods.SecurityImpersonation,
        NativeMethods.TokenPrimary,
        out var duplicatedToken))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx 失败");
    }

    return new SafeNativeHandle(duplicatedToken);
}

static SafeNativeHandle CreateRestrictedMediumToken(IntPtr currentToken)
{
    var sidPointers = new IntPtr[2];
    IntPtr sidAttributes = IntPtr.Zero;
    IntPtr restrictedToken = IntPtr.Zero;
    try
    {
        sidPointers[0] = AllocateSid(NativeMethods.AdministratorsSid);
        sidPointers[1] = AllocateSid(NativeMethods.PowerUsersSid);
        var sidAttributeSize = Marshal.SizeOf<NativeMethods.SidAndAttributes>();
        sidAttributes = Marshal.AllocHGlobal(sidAttributeSize * sidPointers.Length);
        for (var index = 0; index < sidPointers.Length; index++)
        {
            Marshal.StructureToPtr(
                new NativeMethods.SidAndAttributes { Sid = sidPointers[index], Attributes = 0 },
                IntPtr.Add(sidAttributes, index * sidAttributeSize),
                false);
        }

        if (!NativeMethods.CreateRestrictedToken(
            currentToken,
            NativeMethods.DisableMaxPrivilege | NativeMethods.LuaToken,
            (uint)sidPointers.Length,
            sidAttributes,
            0,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            out restrictedToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRestrictedToken 失败");
        }

        var result = new SafeNativeHandle(restrictedToken);
        restrictedToken = IntPtr.Zero;
        try
        {
            SetMediumIntegrity(result.Handle);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
    finally
    {
        if (restrictedToken != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(restrictedToken);
        }

        if (sidAttributes != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(sidAttributes);
        }

        foreach (var sid in sidPointers)
        {
            if (sid != IntPtr.Zero)
            {
                NativeMethods.LocalFree(sid);
            }
        }
    }
}

static void SetMediumIntegrity(IntPtr token)
{
    var mediumSid = AllocateSid(NativeMethods.MediumIntegritySid);
    var labelBuffer = IntPtr.Zero;
    try
    {
        var label = new NativeMethods.TokenMandatoryLabel
        {
            Label = new NativeMethods.SidAndAttributes
            {
                Sid = mediumSid,
                Attributes = NativeMethods.SeGroupIntegrity,
            },
        };
        var labelLength = checked(
            Marshal.SizeOf<NativeMethods.TokenMandatoryLabel>()
            + (int)NativeMethods.GetLengthSid(mediumSid));
        labelBuffer = Marshal.AllocHGlobal(labelLength);
        Marshal.StructureToPtr(label, labelBuffer, false);
        if (!NativeMethods.SetTokenInformation(
            token,
            NativeMethods.TokenIntegrityLevel,
            labelBuffer,
            labelLength))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenIntegrityLevel) 失败");
        }
    }
    finally
    {
        if (labelBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(labelBuffer);
        }

        NativeMethods.LocalFree(mediumSid);
    }
}

static bool TryGetLinkedToken(IntPtr token, out IntPtr linkedToken)
{
    linkedToken = IntPtr.Zero;
    NativeMethods.GetTokenInformation(
        token,
        NativeMethods.TokenLinkedToken,
        IntPtr.Zero,
        0,
        out var requiredLength);
    if (requiredLength == 0)
    {
        var error = Marshal.GetLastWin32Error();
        if (error is NativeMethods.ErrorInvalidParameter
            or NativeMethods.ErrorNoSuchLogonSession
            or NativeMethods.ErrorInsufficientBuffer
            or 0)
        {
            return false;
        }

        throw new Win32Exception(error, "读取 linked token 所需缓冲区大小失败");
    }

    var buffer = Marshal.AllocHGlobal(requiredLength);
    try
    {
        if (!NativeMethods.GetTokenInformation(
            token,
            NativeMethods.TokenLinkedToken,
            buffer,
            requiredLength,
            out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is NativeMethods.ErrorInvalidParameter or NativeMethods.ErrorNoSuchLogonSession)
            {
                return false;
            }

            throw new Win32Exception(error, "读取 linked token 失败");
        }

        linkedToken = Marshal.ReadIntPtr(buffer);
        return linkedToken != IntPtr.Zero;
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static uint ReadIntegrityLevel(IntPtr token)
{
    var buffer = ReadTokenInformation(token, NativeMethods.TokenIntegrityLevel);
    try
    {
        var label = Marshal.PtrToStructure<NativeMethods.TokenMandatoryLabel>(buffer);
        if (label.Label.Sid == IntPtr.Zero)
        {
            throw new InvalidOperationException("token integrity label 缺少 SID。");
        }

        var subAuthorityCount = Marshal.ReadByte(label.Label.Sid, 1);
        if (subAuthorityCount == 0)
        {
            throw new InvalidOperationException("token integrity label SID 无有效子授权。");
        }

        var ridOffset = 8 + (subAuthorityCount - 1) * sizeof(int);
        return unchecked((uint)Marshal.ReadInt32(label.Label.Sid, ridOffset));
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static bool IsSystemToken(IntPtr token)
{
    return string.Equals(
        ReadUserSid(token),
        NativeMethods.LocalSystemSid,
        StringComparison.OrdinalIgnoreCase);
}

static string ReadUserSid(IntPtr token)
{
    var buffer = ReadTokenInformation(token, NativeMethods.TokenUser);
    try
    {
        var user = Marshal.PtrToStructure<NativeMethods.TokenUserInfo>(buffer);
        return ConvertSidToString(user.User.Sid);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static bool IsSidEnabled(IntPtr token, string expectedSid)
{
    var buffer = ReadTokenInformation(token, NativeMethods.TokenGroups);
    try
    {
        var groupCount = Marshal.ReadInt32(buffer);
        if (groupCount < 0 || groupCount > 4096)
        {
            throw new InvalidOperationException($"token group 数量异常：{groupCount}。");
        }

        var groupOffset = Marshal.OffsetOf<NativeMethods.TokenGroupsHeader>(nameof(NativeMethods.TokenGroupsHeader.Groups)).ToInt32();
        var groupSize = Marshal.SizeOf<NativeMethods.SidAndAttributes>();
        for (var index = 0; index < groupCount; index++)
        {
            var group = Marshal.PtrToStructure<NativeMethods.SidAndAttributes>(
                IntPtr.Add(buffer, groupOffset + index * groupSize));
            if (string.Equals(ConvertSidToString(group.Sid), expectedSid, StringComparison.OrdinalIgnoreCase))
            {
                return (group.Attributes & NativeMethods.SeGroupEnabled) != 0;
            }
        }

        return false;
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static bool HasTokenRestrictions(IntPtr token)
{
    var buffer = ReadTokenInformation(token, NativeMethods.TokenHasRestrictions);
    try
    {
        return Marshal.ReadInt32(buffer) != 0;
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static IntPtr ReadTokenInformation(IntPtr token, int informationClass)
{
    NativeMethods.GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var requiredLength);
    if (requiredLength <= 0)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取 token 信息 {informationClass} 所需缓冲区大小失败");
    }

    var buffer = Marshal.AllocHGlobal(requiredLength);
    try
    {
        if (!NativeMethods.GetTokenInformation(token, informationClass, buffer, requiredLength, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取 token 信息 {informationClass} 失败");
        }

        return buffer;
    }
    catch
    {
        Marshal.FreeHGlobal(buffer);
        throw;
    }
}

static IntPtr AllocateSid(string sid)
{
    if (!NativeMethods.ConvertStringSidToSid(sid, out var sidPointer))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"分配 SID {sid} 失败");
    }

    return sidPointer;
}

static string ConvertSidToString(IntPtr sid)
{
    if (sid == IntPtr.Zero)
    {
        throw new InvalidOperationException("token SID 为空。");
    }

    if (!NativeMethods.ConvertSidToStringSid(sid, out var stringSid))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "转换 token SID 失败");
    }

    try
    {
        return Marshal.PtrToStringUni(stringSid)
            ?? throw new InvalidOperationException("token SID 文本为空。");
    }
    finally
    {
        NativeMethods.LocalFree(stringSid);
    }
}

static string DescribeIntegrity(uint integrity)
{
    return integrity == NativeMethods.MediumIntegrityRid ? "Medium" : $"RID-{integrity}";
}

static string QuoteArgument(string value)
{
    if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
    {
        return value;
    }

    var builder = new StringBuilder(value.Length + 2);
    builder.Append('"');
    var backslashes = 0;
    foreach (var character in value)
    {
        if (character == '\\')
        {
            backslashes++;
            continue;
        }

        if (character == '"')
        {
            builder.Append('\\', backslashes * 2 + 1);
            builder.Append('"');
            backslashes = 0;
            continue;
        }

        builder.Append('\\', backslashes);
        builder.Append(character);
        backslashes = 0;
    }

    builder.Append('\\', backslashes * 2);
    builder.Append('"');
    return builder.ToString();
}

sealed record TokenState(string Mode, uint Integrity, bool Restricted, bool AdministratorEnabled);

sealed class InheritedStandardHandles : IDisposable
{
    public InheritedStandardHandles(SafeNativeHandle input, SafeNativeHandle output, SafeNativeHandle error)
    {
        Input = input;
        Output = output;
        Error = error;
    }

    public SafeNativeHandle Input { get; }

    public SafeNativeHandle Output { get; }

    public SafeNativeHandle Error { get; }

    public void Dispose()
    {
        Error.Dispose();
        Output.Dispose();
        Input.Dispose();
    }
}

sealed class SafeNativeHandle : IDisposable
{
    public SafeNativeHandle(IntPtr handle) => Handle = handle;

    public IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero || Handle == new IntPtr(-1)) return;
        NativeMethods.CloseHandle(Handle);
        Handle = IntPtr.Zero;
    }
}

static class NativeMethods
{
    public const uint TokenAssignPrimary = 0x0001;
    public const uint TokenDuplicate = 0x0002;
    public const uint TokenQuery = 0x0008;
    public const uint TokenAdjustDefault = 0x0080;
    public const uint TokenAdjustSessionId = 0x0100;
    public const uint TokenCreateProcessAccess = TokenAssignPrimary
        | TokenDuplicate
        | TokenQuery
        | TokenAdjustDefault
        | TokenAdjustSessionId;

    public const int TokenUser = 1;
    public const int TokenGroups = 2;
    public const int TokenHasRestrictions = 21;
    public const int TokenLinkedToken = 19;
    public const int TokenIntegrityLevel = 25;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const uint MediumIntegrityRid = 0x2000;
    public const uint DisableMaxPrivilege = 0x00000001;
    public const uint LuaToken = 0x00000004;
    public const uint SeGroupEnabled = 0x00000004;
    public const uint SeGroupIntegrity = 0x00000020;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorNoSuchLogonSession = 1312;
    public const int ErrorInsufficientBuffer = 122;
    public const uint Infinite = 0xFFFFFFFF;
    public const uint WaitObject0 = 0;
    public const uint LogonWithProfile = 0x00000001;
    public const int Logon32LogonInteractive = 2;
    public const int Logon32ProviderDefault = 0;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint DuplicateSameAccess = 0x00000002;
    public const int StdInputHandle = -10;
    public const int StdOutputHandle = -11;
    public const int StdErrorHandle = -12;
    public const int StartfUseStdHandles = 0x00000100;

    public const string AdministratorsSid = "S-1-5-32-544";
    public const string PowerUsersSid = "S-1-5-32-547";
    public const string LocalSystemSid = "S-1-5-18";
    public const string MediumIntegritySid = "S-1-16-8192";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out IntPtr targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateRestrictedToken(
        IntPtr existingTokenHandle,
        uint flags,
        uint disableSidCount,
        IntPtr sidsToDisable,
        uint deletePrivilegeCount,
        IntPtr privilegesToDelete,
        uint restrictedSidCount,
        IntPtr sidsToRestrict,
        out IntPtr newTokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LogonUser(
        string userName,
        string domain,
        string password,
        int logonType,
        int logonProvider,
        out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessWithLogon(
        string userName,
        string domain,
        string password,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenUserInfo
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenGroupsHeader
    {
        public int GroupCount;
        public SidAndAttributes Groups;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int processId;
        public int threadId;
    }
}