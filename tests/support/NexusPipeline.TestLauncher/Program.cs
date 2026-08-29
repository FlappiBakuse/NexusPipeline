using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("[错误] Medium Integrity 测试启动器仅支持 Windows。");
    return 2;
}

var command = args.Length > 0 ? args : Array.Empty<string>();
if (command.Length == 0)
{
    Console.Error.WriteLine("用法：NexusPipeline.TestLauncher <命令> [参数...]");
    return 2;
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

        return RunWithLogon(
            command,
            ciUser,
            ciPassword,
            Environment.GetEnvironmentVariable("NEXUS_CI_TEST_DOMAIN") ?? ".");
    }

    return RunAtMediumIntegrity(command);
}
catch (Exception error)
{
    Console.Error.WriteLine($"[错误] 无法以 Medium Integrity 启动测试：{error.Message}");
    return 1;
}

static int RunAtMediumIntegrity(string[] command)
{
    using var currentToken = OpenCurrentProcessToken();
    using var mediumToken = DuplicateMediumToken(currentToken);
    var commandLine = new StringBuilder(string.Join(" ", command.Select(QuoteArgument)));
    var startupInfo = new NativeMethods.StartupInfo
    {
        cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
    };

    if (!NativeMethods.CreateProcessAsUser(
        mediumToken.Handle,
        null,
        commandLine,
        IntPtr.Zero,
        IntPtr.Zero,
        false,
        0,
        IntPtr.Zero,
        Environment.CurrentDirectory,
        ref startupInfo,
        out var processInfo))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser 失败");
    }

    return WaitForProcess(processInfo);
}

static int RunWithLogon(string[] command, string userName, string password, string domain)
{
    var commandLine = new StringBuilder(string.Join(" ", command.Select(QuoteArgument)));
    var startupInfo = new NativeMethods.StartupInfo
    {
        cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
    };

    if (!NativeMethods.CreateProcessWithLogon(
        userName,
        domain,
        password,
        NativeMethods.LogonWithProfile,
        null,
        commandLine,
        NativeMethods.CreateUnicodeEnvironment,
        IntPtr.Zero,
        Environment.CurrentDirectory,
        ref startupInfo,
        out var processInfo))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessWithLogonW 失败");
    }

    return WaitForProcess(processInfo);
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
    var desiredAccess = NativeMethods.TokenQuery
        | NativeMethods.TokenDuplicate
        | NativeMethods.TokenAssignPrimary
        | NativeMethods.TokenAdjustDefault
        | NativeMethods.TokenAdjustSessionId;

    if (!NativeMethods.OpenProcessToken(
        NativeMethods.GetCurrentProcess(),
        desiredAccess,
        out var token))
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken 失败");
    }

    return new SafeNativeHandle(token);
}

static SafeNativeHandle DuplicateMediumToken(SafeNativeHandle currentToken)
{
    var sourceToken = currentToken.Handle;
    SafeNativeHandle? linkedToken = null;
    try
    {
        if (TryGetLinkedToken(sourceToken, out var linkedTokenHandle))
        {
            linkedToken = new SafeNativeHandle(linkedTokenHandle);
            sourceToken = linkedToken.Handle;
        }

        if (!NativeMethods.DuplicateTokenEx(
            sourceToken,
            NativeMethods.TokenAllAccess,
            IntPtr.Zero,
            NativeMethods.SecurityImpersonation,
            NativeMethods.TokenPrimary,
            out var duplicatedToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx 失败");
        }

        var result = new SafeNativeHandle(duplicatedToken);
        var integrity = ReadIntegrityLevel(result.Handle);
        if (integrity != NativeMethods.MediumIntegrityRid)
        {
            result.Dispose();
            throw new InvalidOperationException($"获得的测试 token 不是 Medium Integrity（RID={integrity}）。");
        }

        return result;
    }
    finally
    {
        linkedToken?.Dispose();
    }
}

static bool TryGetLinkedToken(IntPtr token, out IntPtr linkedToken)
{
    linkedToken = IntPtr.Zero;
    NativeMethods.GetTokenInformation(token, NativeMethods.TokenLinkedToken, IntPtr.Zero, 0, out var requiredLength);
    if (requiredLength == 0)
    {
        return false;
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
            if (error == NativeMethods.ErrorInvalidParameter || error == NativeMethods.ErrorNoSuchLogonSession)
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
    NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredLength);
    if (requiredLength == 0)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 token integrity level 所需缓冲区大小失败");
    }

    var buffer = Marshal.AllocHGlobal(requiredLength);
    try
    {
        if (!NativeMethods.GetTokenInformation(
            token,
            NativeMethods.TokenIntegrityLevel,
            buffer,
            requiredLength,
            out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 token integrity level 失败");
        }

        var label = Marshal.PtrToStructure<NativeMethods.TokenMandatoryLabel>(buffer);
        var subAuthorityCount = Marshal.ReadByte(label.Label.Sid, 1);
        var rid = Marshal.ReadInt32(label.Label.Sid, 8 + (subAuthorityCount - 1) * 4);
        return unchecked((uint)rid);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
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
    public const uint TokenAllAccess = 0x000F01FF;
    public const int TokenLinkedToken = 19;
    public const int TokenIntegrityLevel = 25;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const uint MediumIntegrityRid = 0x2000;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorNoSuchLogonSession = 1312;
    public const uint Infinite = 0xFFFFFFFF;
    public const uint WaitObject0 = 0;
    public const uint LogonWithProfile = 0x00000001;
    public const uint CreateUnicodeEnvironment = 0x00000400;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

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
