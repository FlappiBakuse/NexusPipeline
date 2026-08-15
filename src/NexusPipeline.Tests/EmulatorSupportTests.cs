using NexusPipeline.Services;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>安卓模拟器适配纯逻辑（EmulatorSupport）：ADB 地址校验 / am start 包名解析 / dumpsys 前台解析 / MuMuManager 实例反查。</summary>
public class EmulatorSupportTests
{
    [Theory]
    [InlineData("127.0.0.1:16384", true)]
    [InlineData("192.168.1.10:5555", true)]
    [InlineData("localhost:7555", true)]
    [InlineData("", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.1:", false)]
    [InlineData("127.0.0.1:0", false)]
    [InlineData("127.0.0.1:65536", false)]
    [InlineData(":5555", false)]
    [InlineData("abc:def", false)]
    public void IsValidAdbAddress_Cases(string address, bool expected)
    {
        Assert.Equal(expected, EmulatorSupport.IsValidAdbAddress(address));
    }

    [Theory]
    [InlineData("127.0.0.1:16384", 16384)]
    [InlineData("localhost:7555", 7555)]
    [InlineData("bad", null)]
    [InlineData("", null)]
    public void ParseAdbPort_Cases(string address, int? expected)
    {
        Assert.Equal(expected, EmulatorSupport.ParseAdbPort(address));
    }

    [Theory]
    [InlineData("-n com.example.game/.MainActivity", "com.example.game")]
    [InlineData("-n com.example.game", "com.example.game")]
    [InlineData("-a android.intent.action.MAIN -n com.example.game/.MainActivity -c android.intent.category.LAUNCHER", "com.example.game")]
    [InlineData("", null)]
    [InlineData("-n", null)]
    [InlineData("am start com.example.game/.MainActivity", null)]
    public void ParseAmStartPackage_Cases(string args, string? expected)
    {
        Assert.Equal(expected, EmulatorSupport.ParseAmStartPackage(args));
    }

    [Theory]
    [InlineData("  mCurrentFocus=Window{253a256 u0 com.android.settings/com.android.settings.Settings}", "com.android.settings")]
    [InlineData("  mCurrentFocus=Window{37eb77 u0 app.lawnchair/app.lawnchair.LawnchairLauncher}", "app.lawnchair")]
    [InlineData("    topResumedActivity=ActivityRecord{66aea46 u0 com.example.game/.MainActivity t6}", "com.example.game")]
    [InlineData("no focus line here", null)]
    [InlineData("", null)]
    public void ParseForegroundPackage_Cases(string line, string? expected)
    {
        Assert.Equal(expected, EmulatorSupport.ParseForegroundPackage(line));
    }

    [Fact]
    public void ParseForegroundPackage_MultiLine_TakesFocusLine()
    {
        string output = "WINDOW MANAGER STATE DUMPS\n  mCurrentFocus=Window{a1 u0 com.android.settings/com.android.settings.Settings}\n  mFocusedApp=Window{a1 u0 com.android.settings}";
        Assert.Equal("com.android.settings", EmulatorSupport.ParseForegroundPackage(output));
    }

    [Theory]
    [InlineData("Starting: Intent { cmp=com.android.settings/.Settings }", false)]
    [InlineData("Starting: Intent { cmp=com.example.game/.MainActivity }\nWarning: Activity not started, intent has been delivered to currently running top activity.", false)]
    [InlineData("Starting: Intent { cmp=com.example.game/.MainActivity }\nWarning: Activity not started, its current task has been brought to the front", false)]
    [InlineData("Starting: Intent { cmp=com.android.settings/.Nonexistent }\nError type 3\nError: Activity class {com.android.settings/com.android.settings.Nonexistent} does not exist.", true)]
    [InlineData("Error: Activity not started, unable to resolve Intent { act=android.intent.action.MAIN }", true)]
    public void AmStartFailed_Cases(string output, bool expected)
    {
        Assert.Equal(expected, EmulatorSupport.AmStartFailed(output));
    }

    [Fact]
    public void ParseMuMuVmIndex_MatchesAdbPort()
    {
        const string info = "{\"0\":{\"adb_port\":16384,\"is_main\":true},\"1\":{\"adb_port\":16416,\"is_main\":false}}";
        Assert.Equal("0", EmulatorSupport.ParseMuMuVmIndex(info, 16384));
        Assert.Equal("1", EmulatorSupport.ParseMuMuVmIndex(info, 16416));
    }

    [Fact]
    public void ParseMuMuVmIndex_NoMatch_ReturnsNull()
    {
        const string info = "{\"0\":{\"adb_port\":16384}}";
        Assert.Null(EmulatorSupport.ParseMuMuVmIndex(info, 9999));
    }

    [Fact]
    public void ParseMuMuVmIndex_InvalidJson_ReturnsNull()
    {
        Assert.Null(EmulatorSupport.ParseMuMuVmIndex("not json", 16384));
        Assert.Null(EmulatorSupport.ParseMuMuVmIndex("", 16384));
    }
}
