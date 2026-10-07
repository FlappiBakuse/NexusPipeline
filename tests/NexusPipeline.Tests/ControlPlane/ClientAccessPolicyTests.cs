using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Platform.Storage;
using Xunit;
using WebRequest = NexusPipeline.ControlPlane.Http.WebRequest;
using WebResponse = NexusPipeline.ControlPlane.Http.WebResponse;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class ClientAccessPolicyTests
{
    [Fact]
    public void NatOriginsRequireAuthenticatedRemoteTransportAndExactHttpAuthority()
    {
        WebRequest Request(string host, string? origin, string? address)
        {
            var headers = new NameValueCollection { ["Host"] = host, ["X-Forwarded-For"] = "127.0.0.1" };
            if (origin is not null) headers["Origin"] = origin;
            return new(new Uri("http://" + host + "/api/settings"), "POST", new(), headers, Stream.Null, 0, null, Encoding.UTF8, false,
                address is null ? null : new(IPAddress.Parse(address), 1000));
        }
        foreach (string host in new[] { "nexus.example:58000", "203.0.113.8:58000", "[2001:db8::8]:58000" })
        {
            var request = Request(host, "http://" + host, "192.0.2.9");
            Assert.True(RequestOriginPolicy.Decide(request, "POST", "/api/settings", true, true).Allowed);
            Assert.False(RequestOriginPolicy.Decide(request, "POST", "/api/settings", true, false).Allowed);
            Assert.False(RequestOriginPolicy.Decide(request, "POST", "/api/settings", false, true).Allowed);
            Assert.False(RequestOriginPolicy.Decide(Request(host, "http://" + host, "127.0.0.1"), "POST", "/api/settings", true, true).Allowed);
            Assert.False(RequestOriginPolicy.Decide(Request(host, "http://" + host, null), "POST", "/api/settings", true, true).Allowed);
        }
        foreach (string invalid in new[] { "", "null", "https://nexus.example:58000", "http://nexus.example:58001", "http://nexus.example.evil:58000",
            "http://user@nexus.example:58000", "http://nexus.example:58000/", "http://nexus.example:58000/path", "http://nexus.example:58000?x=1",
            "http://nexus.example:58000#x", "http://nexus.example:58000, http://nexus.example:58000", "http://nexus.example:0" })
            Assert.False(RequestOriginPolicy.Decide(Request("nexus.example:58000", invalid, "192.0.2.9"), "POST", "/api/settings", true, true).Allowed);
        var multiple = Request("nexus.example:58000", "http://nexus.example:58000", "192.0.2.9");
        multiple.Headers.Add("Origin", "http://nexus.example:58000");
        Assert.False(RequestOriginPolicy.Decide(multiple, "POST", "/api/settings", true, true).Allowed);
        Assert.True(RequestOriginPolicy.Decide(Request("localhost:58000", "http://localhost:58000", "::ffff:127.0.0.1"), "POST", "/api/settings", false, false).Allowed);
        var recovery = Request("localhost:58001", "http://localhost:58000", "127.0.0.1");
        Assert.Equal("http://localhost:58000", RequestOriginPolicy.Decide(recovery, "GET", "/api/status", false, false).CorsOrigin);
        Assert.False(RequestOriginPolicy.Decide(recovery, "POST", "/api/settings", false, false).Allowed);
    }

    [Fact]
    public void ScriptBrowserListsOnlyDeclaredRootsAndCannotEscapeToSiblingOrLinkedDirectories()
    {
        string root=Path.Combine(Path.GetTempPath(),"nxp-browser-"+Guid.NewGuid().ToString("N"));
        string allowed=Path.Combine(root,"script"), sibling=Path.Combine(root,"script-other"), link=Path.Combine(allowed,"linked");
        Directory.CreateDirectory(allowed);Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(allowed,"script.cmd"),"owned");
        try
        {
            var start=new ProcessStartInfo("cmd.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("/d");start.ArgumentList.Add("/c");start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");start.ArgumentList.Add(link);start.ArgumentList.Add(sibling);
            using var junction=Process.Start(start)!;
            Assert.True(junction.WaitForExit(5000),"Directory junction creation did not finish");
            Assert.Equal(0,junction.ExitCode);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            var source=new BrowserScripts(new ScriptInstance{RootPath=allowed});
            var capabilities=new BrowserCapabilities();
            var browser=new ScriptFileBrowser(new ScriptQueries(source,new ScriptSpecResolver(capabilities,capabilities)),new FileBrowser());
            Assert.Equal([allowed],browser.Browse(null).Data!.Directories);
            var result=browser.Browse(allowed);
            Assert.True(result.Succeeded);Assert.Null(result.Data!.Parent);
            Assert.DoesNotContain(link,result.Data.Directories);
            Assert.Equal([Path.Combine(allowed,"script.cmd")],result.Data.Files);
            foreach(string forbidden in new[]{root,sibling,link,Path.GetPathRoot(root)!,@"\\server\share"})
                Assert.Equal("fs_path_forbidden",browser.Browse(forbidden).ErrorCode);
            source.Script.RootPath=Path.GetPathRoot(root)!;
            Assert.Empty(browser.Browse(null).Data!.Directories);
        }
        finally
        {
            if(Directory.Exists(link))Directory.Delete(link);
            Directory.Delete(root,true);
        }
    }

    private sealed class BrowserScripts(ScriptInstance script):IScriptRepository
    {
        internal ScriptInstance Script {get;}=script;
        public ScriptInstance? FindById(string id)=>Script.Id==id?Script:null;
        public IReadOnlyList<ScriptInstance> Snapshot()=>[Script];
    }
    private sealed class BrowserCapabilities:IPluginCapabilityResolver,IPluginAvailability
    {
        public bool SupportsEmulator(string name)=>false;
        public bool HasCapability(string name,string capability)=>false;
        public ScriptProfile? ResolveProfile(string name,string root,IReadOnlyDictionary<string,string>? inputs=null)=>null;
        public IReadOnlyList<string> GetMissingConfigCandidates(string name,string root,IReadOnlyDictionary<string,string>? inputs)=>[];
        public bool IsKnownPlugin(string name)=>false;
        public bool IsDataSpecializedPlugin(string name)=>false;
        public bool IsEnabled(string name)=>false;
    }

    [Fact]
    public void TransportIdentityNormalizesLoopbackAndIgnoresForwardingHeaders()
    {
        foreach (string address in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1" })
            Assert.Equal(PluginClientConnectionKind.Local, RequestAccessPolicy.Connection(Context(address).Context.Request));
        var remote = Context("192.0.2.1");
        remote.Context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        remote.Context.Request.Headers["User-Agent"] = "Electron";
        Assert.Equal(PluginClientConnectionKind.Remote, RequestAccessPolicy.Connection(remote.Context.Request));
        Assert.Equal(PluginClientConnectionKind.Unknown, RequestAccessPolicy.Connection(Context(null).Context.Request));
        foreach (var kind in Enum.GetValues<PluginClientConnectionKind>())
        {
            Assert.True(RequestAccessPolicy.Decide(kind, PluginOperationAccess.General).Allowed);
            Assert.Equal(kind == PluginClientConnectionKind.Local, RequestAccessPolicy.Decide(kind, PluginOperationAccess.HostFilePicker).Allowed);
            Assert.Equal(kind == PluginClientConnectionKind.Local, RequestAccessPolicy.Decide(kind, PluginOperationAccess.NativeConfigEditor).Allowed);
        }
    }

    [Fact]
    public async Task RemotePickerRejectsBeforeAnyDirectoryProbeOrNativeCallback()
    {
        var remote = Context("192.0.2.1"); var picker = new Picker();
        await ApiNativeDialogHandler.Handle(remote.Context, "POST", ["native-dialog"], "{\"kind\":\"file\",\"requireInitialDirectory\":true}", picker);
        Assert.Equal(403, remote.Context.Response.StatusCode);
        Assert.Equal(0, picker.Calls);
        using var json = JsonDocument.Parse(remote.Bytes.ToArray());
        Assert.Equal("operation_requires_local", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("host_file_picker_requires_local", json.RootElement.GetProperty("details").GetProperty("denyReason").GetString());
    }

    [Fact]
    public void RegistrationsRejectMissingUnknownAndConflictingAccessAndFreezeActions()
    {
        var web = new PluginWebApiRegistry();
        Assert.Throws<InvalidDataException>(() => web.Register("test", new("POST", "save", 0, (_, _) => ValueTask.FromResult(PluginWebApiResponse.Empty()))));
        using var route = web.Register("test", new("POST", "save", PluginOperationAccess.General, (_, _) => ValueTask.FromResult(PluginWebApiResponse.Empty())));
        Assert.Throws<InvalidOperationException>(() => web.Register("test", new("post", "save", PluginOperationAccess.HostFilePicker, (_, _) => ValueTask.FromResult(PluginWebApiResponse.Empty()))));
        var actions = new Dictionary<string, PluginOperationAccess> { ["choose"] = PluginOperationAccess.HostFilePicker };
        var ui = new PluginUiContributionRegistry();
        var contribution = PluginUiContribution.Card(new(PluginOperationAccess.General, null, actions), "card", PluginUiSlots.SettingsCards,
            "Title", (_, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(new()),
            actionHandler: (_, _, _, _) => ValueTask.FromResult<System.Text.Json.Nodes.JsonObject?>(new()));
        using var registration = ui.Register("test", "Test", contribution);
        actions["choose"] = PluginOperationAccess.General;
        Assert.Equal(PluginOperationAccess.HostFilePicker, ui.Snapshot().Single().Contribution.Access.Actions["choose"]);
        Assert.Throws<InvalidDataException>(() => ui.Register("test", "Test", contribution with { Id = "bad", Access = new(null, null, actions) }));
        Assert.Throws<InvalidDataException>(() => ui.Register("test", "Test", contribution with { Id = "bad", Access = new(PluginOperationAccess.General, null, new Dictionary<string, PluginOperationAccess> { ["a"] = 0 }) }));
    }

    [Fact]
    public async Task CapabilitiesExposeOnlyTheThreeValidatedOperationsWithoutCaching()
    {
        var remote = Context("192.0.2.1");
        await ApiClientCapabilitiesHandler.Handle(remote.Context, "GET", ["client-capabilities"]);
        Assert.Equal("no-store", remote.Context.Response.Headers["Cache-Control"]);
        using var json = JsonDocument.Parse(remote.Bytes.ToArray());
        Assert.Equal("remote", json.RootElement.GetProperty("connectionKind").GetString());
        var operations = json.RootElement.GetProperty("operations");
        Assert.Equal(3, operations.EnumerateObject().Count());
        Assert.True(operations.GetProperty("general").GetProperty("allowed").GetBoolean());
        Assert.False(operations.GetProperty("nativeConfigEditor").GetProperty("allowed").GetBoolean());
    }

    private sealed class Picker : INativePathPicker
    {
        public int Calls;
        public bool IsExistingDirectory(string? path) { Calls++; return true; }
        public Task<string?> PickAsync(NativePathPickerRequest request) { Calls++; return Task.FromResult<string?>("unused"); }
    }
    private static (WebContext Context, MemoryStream Bytes) Context(string? address)
    {
        var bytes = new MemoryStream();
        var request = new WebRequest(new Uri("http://localhost:58731/"), "POST", new(), new NameValueCollection(), Stream.Null, 0, null, Encoding.UTF8, false,
            address is null ? null : new IPEndPoint(IPAddress.Parse(address), 1000));
        return (new WebContext(request, new WebResponse(bytes)), bytes);
    }
}
