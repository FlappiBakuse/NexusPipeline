using System.Text.Json.Nodes;
using Jint.Runtime;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using Xunit;

namespace NexusPipeline.Tests.Configuration;

public sealed class JintScriptHostTests
{
    [Fact]
    public void DeepRecursionFailsWithoutEndingHostProcess()
    {
        Assert.Throws<RecursionDepthOverflowException>(() => JintScriptHost.Create(TimeSpan.FromSeconds(1), default).Execute("function f(){return f()} f();", JintScriptHostProfile.Judge));
        Assert.Throws<RecursionDepthOverflowException>(() => JintScriptHost.Create(TimeSpan.FromSeconds(1), default).Execute("const x={get value(){return this.value}};x.value;", JintScriptHostProfile.Judge));
        var healthy = JintScriptHost.Create(TimeSpan.FromSeconds(1), default);
        healthy.Execute("console.log('alive');", JintScriptHostProfile.Judge);
        Assert.Equal("alive", Assert.Single(healthy.Outputs));
    }

    [Fact]
    public void InteropPreservesArrayCopiesListWritesAndFreshIdentity()
    {
        int[] array = [1, 2];
        var list = new List<int> { 3, 4 };
        var host = JintScriptHost.Create(TimeSpan.FromSeconds(1), default);
        host.SetValue("__nexusListFiles", new Func<int[]>(() => array));
        host.SetValue("list", list);
        host.Execute("const a=nexus.listFiles(), b=nexus.listFiles(); a[0]=9; list[0]=8; console.log(JSON.stringify([Array.isArray(a),Array.isArray(list),a===b,a.length,b[0],Array.from(list)]));", JintScriptHostProfile.Judge);
        Assert.Equal("[true,false,false,2,1,[8,4]]", Assert.Single(host.Outputs));
        Assert.Equal(1, array[0]);
        Assert.Equal(8, list[0]);
    }

    [Fact]
    public void ConfigurationInputAndExceptionsKeepPublicContract()
    {
        var host = JintScriptHost.Create(TimeSpan.FromSeconds(1), default);
        host.SetInput("{\"text\":\"中文😀\",\"enabled\":false}");
        host.Execute("__nexusLog(JSON.stringify(nexus.input));", JintScriptHostProfile.ConfigValidation);
        Assert.Equal("{\"text\":\"中文😀\",\"enabled\":false}", Assert.Single(host.Outputs));
        Assert.Throws<JavaScriptException>(() => JintScriptHost.Create(TimeSpan.FromSeconds(1), default).Execute("throw new Error('fixture_error');", JintScriptHostProfile.Judge));
    }

    [Fact]
    public void StatementAndWallClockLimitsRejectFiniteOwnedInputs()
    {
        Assert.Throws<StatementsCountOverflowException>(() => JintScriptHost.Create(TimeSpan.FromSeconds(1), default, 100).Execute("for(let i=0;i<10000;i++){}", JintScriptHostProfile.Judge));
        Assert.Throws<TimeoutException>(() => JintScriptHost.Create(TimeSpan.FromMilliseconds(30), default).Execute("while(true){}", JintScriptHostProfile.Judge));
    }

    [Fact]
    public void CancellationDeadlineSpansGlueAndScript()
    {
        using var cancellation = new CancellationTokenSource();
        var host = JintScriptHost.Create(TimeSpan.FromSeconds(1), cancellation.Token);
        host.SetValue("__nexusListFiles", new Func<string[]>(() => { cancellation.Cancel(); return []; }));
        Assert.Throws<ExecutionCanceledException>(() => host.Execute("nexus.listFiles();for(let i=0;i<100000;i++){}", JintScriptHostProfile.Judge));
    }

    [Fact]
    public async Task ProtocolResourcesAndOneJsonResultFailClosed()
    {
        var result = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>("let unavailable=false;try{nexus.readConfig('missing')}catch(e){unavailable=e.message==='config_unavailable'}console.log({unavailable,input:nexus.input.text});", new { text = "中文😀" }, _ => throw new IOException(), _ => throw new IOException(), true, default);
        Assert.True(result["unavailable"]!.GetValue<bool>());
        Assert.Equal("中文😀", result["input"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidDataException>(() => TaskProtocolScriptRunner.ExecuteAsync<JsonObject>("console.log({});console.log({});", new { }, _ => "{}", _ => "{}", true, default));
    }
}
