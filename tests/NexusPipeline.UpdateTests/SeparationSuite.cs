using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using NexusPipeline.Maintenance;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Updater;

internal static partial class SeparationSuite
{
    private static string Output = "", ApplicationRoot = "", Target = "", Tools = "";
    private static readonly List<object> Cases = [];
    internal static async Task Run(string[] args)
    {
        var options = new Dictionary<string,string>();
        for (int i=0;i<args.Length;i+=2) if (i+1>=args.Length || !options.TryAdd(args[i],Path.GetFullPath(args[i+1]))) throw new ArgumentException("Invalid separation options");
        Output=options["--output"]; ApplicationRoot=options["--application"]; Target=options["--target"]; Tools=options["--tools"];
        if (Directory.Exists(Output)) throw new IOException("Separation report exists");
        Directory.CreateDirectory(Output);
        var currentIdentity=ApplicationPayload.Validate(ApplicationRoot,"0.17.4");var targetIdentity=ApplicationPayload.Validate(Target,"0.17.5");
        File.WriteAllText(Path.Combine(Output,"input-identities.json"),JsonSerializer.Serialize(new{currentVersion=currentIdentity.ProductVersion,currentBuildId=currentIdentity.BuildId,currentHostSha256=Hash(Path.Combine(ApplicationRoot,"NexusPipeline.exe")),currentUpdaterSha256=Hash(Path.Combine(ApplicationRoot,UpdateRecipe.UpdateEntry)),targetVersion=targetIdentity.ProductVersion,targetBuildId=targetIdentity.BuildId,targetHostSha256=Hash(Path.Combine(Target,"NexusPipeline.exe")),targetUpdaterSha256=Hash(Path.Combine(Target,UpdateRecipe.UpdateEntry)),probeSha256=Hash(typeof(SeparationSuite).Assembly.Location),updaterImplementationSha256=Hash(typeof(UpdateWindow).Assembly.Location),maintenanceImplementationSha256=Hash(typeof(UpdateEngine).Assembly.Location)}));
        Environment.SetEnvironmentVariable("NEXUS_SYSTEM_RUNTIME_NAME","native-"+Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE"));
        string temporary=Path.Combine(Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT")!,"tmp");Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEMP",temporary);Environment.SetEnvironmentVariable("TMP",temporary);
        await Case("W02-U03-final-validation-cancellation",async()=>{await DownloadRace(false);await DownloadRace(false,cancel:false);});
        await Case("U04-unknown-cleanup-bytes",async()=>{foreach(string mode in new[]{"unknown","changed","locked"})await DownloadRace(true,mode);});
        await Case("W03-local-remote-message-projection",Projection);
        await Case("E03-independent-updater",Independent);
        await Case("E04-W04-remote-identity-and-http-lifetime",RemoteLifetime);
        await NativeCases(options);
        Require(Cases.Count==17,"All seventeen maintenance scenarios must complete");
        File.WriteAllText(Path.Combine(Output,"result.json"),JsonSerializer.Serialize(new { status="PASS", passed=Cases.Count, failed=0, skipped=0, cases=Cases,
            classification="LOCAL_DIAGNOSTIC",productionUacTested=false },new JsonSerializerOptions { WriteIndented=true }));
    }
    private static async Task Case(string name,Func<Task> action)
    {
        Console.WriteLine("START "+name);
        try { await action(); Cases.Add(new { name,status="PASS" }); Console.WriteLine("PASS "+name);
            foreach(string directory in new[]{"independent","remote","version-source","local-close-False","local-close-True","host-download","portable-missing","tampered"})RetireCommon(Path.Combine(Output,directory));
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(Output,"failure.txt"),name+"\n"+error); throw; }
    }
    private static void Require(bool value,string message) { if(!value)throw new InvalidDataException(message); }
    private static async Task Until(Func<bool> condition,int milliseconds=15000)
    {
        var timer=Stopwatch.StartNew(); while(!condition()) { if(timer.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Observation deadline"); await Task.Delay(25); }
    }
    private static string Hash(string path) { using var file=File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void RetireCommon(string root)
    {
        string manifest=Path.Combine(root,ApplicationPayload.ManifestPath);if(!File.Exists(manifest))return;
        foreach(var process in Process.GetProcesses())using(process)try{if(process.MainModule?.FileName.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)==true)return;}catch(Exception error)when(error is System.ComponentModel.Win32Exception or InvalidOperationException){}
        PayloadPathSafety.RequireLinkFree(root);Require(Path.GetFullPath(root).StartsWith(Output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"owned cleanup root");
        using var document=JsonDocument.Parse(File.ReadAllBytes(manifest));var removed=new List<object>();
        foreach(var item in document.RootElement.GetProperty("files").EnumerateArray()) {
            string name=item.GetProperty("path").GetString()!,path=Path.GetFullPath(Path.Combine(root,name));Require(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"owned payload path");PayloadPathSafety.RequireLinkFree(path);
            if(File.Exists(path)&&Hash(path)==item.GetProperty("sha256").GetString())removed.Add(new{path=name,sha256=Hash(path),sizeBytes=new FileInfo(path).Length});
        }
        File.WriteAllText(Path.Combine(Output,Path.GetFileName(root)+"-retired-payload.json"),JsonSerializer.Serialize(new{sourceManifest=document.RootElement.Clone(),removed,reason="Completed isolated test payload; runtime, unknown bytes and original reports retained"}));
        foreach(var item in document.RootElement.GetProperty("files").EnumerateArray()){string path=Path.Combine(root,item.GetProperty("path").GetString()!);if(File.Exists(path)&&Hash(path)==item.GetProperty("sha256").GetString())File.Delete(path);}
        File.Delete(manifest);
        string resources=Path.Combine(root,"resources");if(Directory.Exists(resources))foreach(string directory in Directory.GetDirectories(resources,"*",SearchOption.AllDirectories).OrderByDescending(p=>p.Length).Append(resources))if(!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);
    }
    private static void CopyApplication(string source,string target)
    {
        foreach(string name in ApplicationPayload.Files(source).Select(f=>f.Path).Append(ApplicationPayload.ManifestPath)) {
            string file=Path.Combine(target,name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(Path.Combine(source,name),file,true);
        }
    }
    private static IEnumerable<Control> Controls(Control root) { foreach(Control child in root.Controls) { yield return child; foreach(var item in Controls(child))yield return item; } }
    private sealed class Window : IDisposable
    {
        internal UpdateWindow Form=null!;
        internal TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously), Ended=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        internal Window(IUpdateSession session,bool chinese=false):this(()=>session,chinese) { }
        internal Window(Func<IUpdateSession> create,bool chinese=false)
        {
            _thread=new Thread(()=> { try {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException,true);
                using var session=create();
                using var form=new UpdateWindow(session,chinese) { ShowInTaskbar=false,Opacity=0 }; Form=form;
                form.Shown+=(_,_)=>Started.TrySetResult(); Application.Run(form); Ended.TrySetResult();
            } catch(Exception e) { Started.TrySetException(e);Ended.TrySetException(e); } }) { IsBackground=true };
            _thread.SetApartmentState(ApartmentState.STA);_thread.Start();
        }
        internal async Task<T> On<T>(Func<UpdateWindow,T> action) { await Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); var done=new TaskCompletionSource<T>(); Form.BeginInvoke(new Action(()=> { try { done.SetResult(action(Form)); }catch(Exception e){done.SetException(e);} })); return await done.Task; }
        internal Task<string> Primary()=>On(f=>Controls(f).OfType<Button>().Last().Text);
        internal Task<bool> PrimaryEnabled()=>On(f=>Controls(f).OfType<Button>().Last().Enabled);
        internal Task<string> Message()=>On(f=>Controls(f).OfType<TextBox>().Single().Text);
        internal Task Click()=>On(f=> { var button=Controls(f).OfType<Button>().Last();Require(button.Enabled,"Primary must be enabled");button.PerformClick();return true; });
        internal Task Close()=>On(f=> { f.Close(); return true; });
        public void Dispose() { if(!Ended.Task.IsCompleted && Form is {IsDisposed:false})Form.BeginInvoke(new Action(Form.Close));_thread.Join(TimeSpan.FromSeconds(3)); }
    }
    private sealed class EngineSession(UpdateEngine engine) : IUpdateSession
    {
        internal int Downloads;
        internal TaskCompletionSource Accepted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UpdateWindowState> ReadStatusAsync(CancellationToken token) { token.ThrowIfCancellationRequested();return Task.FromResult(UpdateSessions.Project(engine.GetStatus())); }
        public async Task CommandAsync(string action,CancellationToken token) { if(action=="download"){Downloads++;Require(engine.StartDownload("test",token).Succeeded,"download accepted");await Accepted.Task.WaitAsync(token);}else if(action=="cancel")engine.CancelDownload(); }
        public Task CloseAsync()=>engine.CancelAndWaitAsync();public void Dispose() { }
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token); }
    private static async Task DownloadRace(bool conflict,string mode="unknown",bool cancel=true)
    {
        string root=Path.Combine(Output,conflict?"cleanup-"+mode:cancel?"validation-race":"validation-ready");Directory.CreateDirectory(root);
        byte[] zip;using(var stream=new MemoryStream()) { using(var archive=new ZipArchive(stream,ZipArchiveMode.Create,true)) { var item=archive.CreateEntry("NexusPipeline.exe");using var output=item.Open();output.Write(Encoding.UTF8.GetBytes("controlled application input")); }zip=stream.ToArray(); }
        var validation=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string digest=Convert.ToHexStringLower(SHA256.HashData(zip));
        var catalog=JsonSerializer.Serialize(new[] { new { id=1,tag_name="v0.17.5",draft=false,prerelease=true,body="release notes",assets=new[] { new { id=1,state="uploaded",name="NexusPipeline-v0.17.5-win-x64.zip",size=zip.Length,digest="sha256:"+digest,browser_download_url="https://fixture.example/NexusPipeline-v0.17.5-win-x64.zip" } } } });
        using var handler=new Handler((request,token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=request.RequestUri!.AbsolutePath.EndsWith(".zip")?new ByteArrayContent(zip):new StringContent(request.RequestUri.AbsolutePath.EndsWith("update-policy.json")?"{\"schemaVersion\":1,\"repository\":\"FlappiBakuse/NexusPipeline\",\"barriers\":[]}":catalog) }));
        var engine=new UpdateEngine(()=>new("https://fixture.example/releases","prerelease"),"0.17.4",root,(_,_,_)=>new HttpClient(handler,false),
            (directory,_,_)=> { if(directory.Contains("staging")) { validation.TrySetResult();release.Task.GetAwaiter().GetResult(); } return new(new string('a',64),new string('b',64),new string('c',64),"g0170"); });
        var session=new EngineSession(engine); await engine.CheckAsync("test");Require(engine.GetStatus().CanDownload,"discovery verified");
        using var window=new Window(session);await window.Started.Task;await Until(()=>window.Primary().GetAwaiter().GetResult()=="Download");
        await window.On(f=> { var b=Controls(f).OfType<Button>().Last();b.PerformClick();b.PerformClick();return true; });
        session.Accepted.TrySetResult();
        await validation.Task.WaitAsync(TimeSpan.FromSeconds(10));await Until(()=>engine.GetStatus().Progress==100);
        Require(await window.Primary()=="Cancel download","100% must remain cancellable before validation");Require(session.Downloads==1,"double click accepted once");
        string? unknown=null,preservedHash=null;FileStream? locked=null;
        if(conflict) { unknown=mode=="unknown"?Path.Combine(Directory.GetDirectories(Path.Combine(root,".nxp-update","staging")).Single(),"unknown.bin"):Directory.GetFiles(Path.Combine(root,".nxp-update"),"*.zip.g*").Single();
            if(mode!="locked")File.WriteAllBytes(unknown,[0,255,42]);preservedHash=Hash(unknown);if(mode is "unknown" or "locked")locked=new FileStream(unknown,FileMode.Open,FileAccess.Read,mode=="unknown"?FileShare.None:FileShare.Read); }
        Task completion=engine.WaitForCurrentOperationAsync();
        if(cancel){Require(engine.CancelDownload(),"cancel accepted");Require(!engine.CancelDownload(),"repeat cancel is idempotent");Require(engine.State==UpdateState.Cancelling&&!completion.IsCompleted,"completion held through validation");}
        release.TrySetResult();await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Require(engine.State==(conflict?UpdateState.RecoveryPending:cancel?UpdateState.Idle:UpdateState.Ready),"stable terminal state after final validation");
        if(!cancel)await Until(()=>window.Primary().GetAwaiter().GetResult()=="Apply & restart");
        locked?.Dispose();if(unknown is not null)Require(Hash(unknown)==preservedHash,"conflicting bytes preserved");
        await window.Close();await window.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.WriteAllText(Path.Combine(root,"observation.json"),JsonSerializer.Serialize(new { session.Downloads,networkProgress=100,finalState=engine.State.ToString(),completionHeld=true,unknownBytesPreserved=conflict }));
    }
    private sealed class MutableSession : IUpdateSession
    {
        internal UpdateWindowState State=new("0.17.4","0.17.5","idle",true,0,"");
        public Task<UpdateWindowState> ReadStatusAsync(CancellationToken token)=>Task.FromResult(State);
        public Task CommandAsync(string action,CancellationToken token)=>Task.CompletedTask;
        public Task CloseAsync()=>Task.CompletedTask;public void Dispose() { }
    }
    private sealed class ObservedSession(IUpdateSession session) : IUpdateSession
    {
        internal int Active,Maximum;
        public async Task<UpdateWindowState> ReadStatusAsync(CancellationToken token) { int active=Interlocked.Increment(ref Active);Maximum=Math.Max(Maximum,active);try{return await session.ReadStatusAsync(token);}finally{Interlocked.Decrement(ref Active);} }
        public Task CommandAsync(string action,CancellationToken token)=>session.CommandAsync(action,token);
        public Task CloseAsync()=>session.CloseAsync();public void Dispose()=>session.Dispose();
    }
    private static async Task Projection()
    {
        foreach(bool chinese in new[] {false,true}) {
            var snapshot=new UpdateStatusSnapshot(UpdateState.Idle,null,0,0,"raw response with fixture credential", "0.17.4","0.17.5",true,"prerelease",true,"release notes",true);
            var session=new MutableSession { State=UpdateSessions.Project(snapshot) };
            Require(session.State.Message=="update_failed","local status hides raw transport response");
            using var window=new Window(session,chinese);await window.Started.Task;await Until(()=>window.Message().GetAwaiter().GetResult().Contains(chinese?"更新操作失败":"update operation failed"));
            session.State=session.State with {Message="release notes"};await Until(()=>window.Message().GetAwaiter().GetResult()=="release notes");
            session.State=session.State with {Message=""};await Until(()=>window.Message().GetAwaiter().GetResult()=="");
            session.State=session.State with {Message="installer.uninstall_requires_setup"};await Until(()=>window.Message().GetAwaiter().GetResult().Contains(chinese?"修复":"repair"));
            Require(UpdateSessions.Message("",null," ","notes")=="notes","empty fields preserve normal description");await window.Close();await window.Ended.Task;
        }
    }
    private static async Task Independent()
    {
        string root=Path.Combine(Output,"independent");CopyApplication(ApplicationRoot,root);Directory.CreateDirectory(Path.Combine(root,"config"));
        File.WriteAllText(Path.Combine(root,"config/settings.json"),"{\"HostLocale\":\"en-US\",\"UpdateCheckEnabled\":false}");
        var locks=new List<FileStream>();foreach(string name in new[]{"users.json","scripts.json","queues.json"}) { string file=Path.Combine(root,"config",name);File.WriteAllText(file,"invalid unrelated business configuration");locks.Add(new(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)); }
        using var updater=Process.Start(new ProcessStartInfo(Path.Combine(root,UpdateRecipe.UpdateEntry)) { WorkingDirectory=root,UseShellExecute=false })!;
        try {
            await Until(()=> { updater.Refresh();return updater.MainWindowHandle!=IntPtr.Zero; });Require(!updater.HasExited,"independent window is running");
            Require(!File.Exists(new RuntimeStateLayout(root).ServicePidPath)&&!File.Exists(new RuntimeStateLayout(root).WebPortPath),"no service initialized");
            Require(!Directory.Exists(Path.Combine(root,"plugins"))&&!Directory.Exists(Path.Combine(root,"history")),"no business initialization");
            File.WriteAllText(Path.Combine(root,"observation.json"),JsonSerializer.Serialize(new { updater.Id,image=updater.MainModule!.FileName,window=updater.MainWindowTitle,exclusiveBusinessFiles=locks.Count,servicePidAbsent=true,webPortAbsent=true }));
            updater.CloseMainWindow();await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Require(updater.ExitCode==0,"independent updater closes cleanly");
        } finally { foreach(var file in locks)file.Dispose();if(!updater.HasExited){Require(updater.MainModule!.FileName==Path.Combine(root,UpdateRecipe.UpdateEntry),"owned updater identity");updater.Kill();await updater.WaitForExitAsync();} }
    }
    private sealed class Loopback : IDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource _stop=new();
        internal readonly List<string> Requests=[];internal Func<string,Task<(int,byte[])>> Response=null!;internal int Port;
        internal Func<string,NetworkStream,Task<bool>>? StreamResponse;
        internal Loopback() { _listener.Start();Port=((IPEndPoint)_listener.LocalEndpoint).Port;_ = Accept(); }
        private async Task Accept() { try { while(true){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_ = Serve(client);} }catch(OperationCanceledException){} }
        private async Task Serve(TcpClient client) {
            using(client)try { var stream=client.GetStream();var reader=new StreamReader(stream,Encoding.ASCII,false,8192,true);string? line=await reader.ReadLineAsync(_stop.Token);if(line is null)return;string path=line.Split(' ')[1];while(!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))){}
                lock(Requests)Requests.Add(path);if(StreamResponse is not null&&await StreamResponse(path,stream))return;var (code,body)=await Response(path);await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),_stop.Token);await stream.WriteAsync(body,_stop.Token);
            }catch(Exception e)when(e is IOException or OperationCanceledException or SocketException){} }
        public void Dispose(){_stop.Cancel();_listener.Stop();_stop.Dispose();}
    }
    private static async Task RemoteLifetime()
    {
        string root=Path.Combine(Output,"remote");CopyApplication(ApplicationRoot,root);
        using var host=await StartHost(root);
        var layout=new RuntimeStateLayout(root);string pid=File.ReadAllText(layout.ServicePidPath),port=File.ReadAllText(layout.WebPortPath);
        try {
            using(var real=new RemoteUpdateSession(root,"0.17.4",int.Parse(port)))Require((await real.ReadStatusAsync(default)).Current=="0.17.4","real Host API");
            foreach(string mutation in new[]{"pid","image","port"}) {
                File.WriteAllText(layout.ServicePidPath,mutation=="pid"?"2147483647":mutation=="image"?Environment.ProcessId.ToString():pid);
                File.WriteAllText(layout.WebPortPath,mutation=="port"?"1":port);
                Require(InstallerServiceHandoff.FindOwningServicePort(root,"0.17.4") is null,"wrong "+mutation+" rejected");
                try{using var rejected=UpdateSessions.Open(root);throw new InvalidDataException("Locked invalid owner became a local session");}catch(IOException){}
            }
            File.WriteAllText(layout.ServicePidPath,pid);
            using var server=new Loopback();File.WriteAllText(layout.WebPortPath,server.Port.ToString());
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string status="{\"current\":\"0.17.4\",\"latest\":\"0.17.5\",\"state\":\"idle\",\"canDownload\":true,\"notes\":\"safe notes\"}";
            server.Response=async path=> { if(path.StartsWith("/api/status"))return (200,JsonSerializer.SerializeToUtf8Bytes(new { ready=true,version="0.17.4",installationGeneration="g0170",actualPort=server.Port }));entered.TrySetResult();await release.Task;return(200,Encoding.UTF8.GetBytes(status)); };
            using var remote=new RemoteUpdateSession(root,"0.17.4",server.Port);
            using(var cancel=new CancellationTokenSource()) { var read=remote.ReadStatusAsync(cancel.Token);await entered.Task;var timer=Stopwatch.StartNew();cancel.Cancel();try{await read;throw new InvalidDataException("HTTP was not cancelled");}catch(OperationCanceledException){}Require(timer.ElapsedMilliseconds<3000,"real HTTP token cancellation"); }
            var bounded=remote.ReadStatusAsync(default);var elapsed=Stopwatch.StartNew();try{await bounded;throw new InvalidDataException("HTTP deadline missing");}catch(TimeoutException error)when(error.Message=="update.service_timeout"){}elapsed.Stop();Require(elapsed.Elapsed<TimeSpan.FromSeconds(15),"finite HTTP deadline");
            using(var window=new Window(remote)) { await window.Started.Task;await Task.Delay(200);await window.Close();await window.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));release.TrySetResult();await Task.Delay(200);Require(!host.HasExited,"remote close retains real Host"); }
            string Identity()=>JsonSerializer.Serialize(new { ready=true,version="0.17.4",installationGeneration="g0170",actualPort=server.Port });
            foreach(bool chinese in new[]{false,true}) {
                bool error=true;
                server.Response=path=>Task.FromResult((200,Encoding.UTF8.GetBytes(path.StartsWith("/api/status")?Identity():JsonSerializer.Serialize(new { current="0.17.4",latest="0.17.5",state="idle",canDownload=true,progress=(int?)null,errorCode=error?"update_failed":"",policyError="",pendingError="",installerToolsWarning="",notes="safe notes" }))));
                using var projected=new RemoteUpdateSession(root,"0.17.4",server.Port);
                Require((await projected.ReadStatusAsync(default)).Message=="update_failed","remote error matches safe local projection");
                using var window=new Window(projected,chinese);await window.Started.Task;await Until(()=>window.Message().GetAwaiter().GetResult().Contains(chinese?"更新操作失败":"update operation failed"));
                error=false;await Until(()=>window.Message().GetAwaiter().GetResult()=="safe notes");await window.Close();await window.Ended.Task;
            }
            var oldEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var oldRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int reads=0;string current="idle";
            server.Response=async path=> {
                if(path.StartsWith("/api/status"))return(200,Encoding.UTF8.GetBytes(Identity()));
                if(path=="/api/update/download"){current="downloading";return(200,Encoding.UTF8.GetBytes("{\"ok\":true}"));}
                int index=Interlocked.Increment(ref reads);if(index==2){oldEntered.TrySetResult();await oldRelease.Task;return(200,Encoding.UTF8.GetBytes("{\"current\":\"0.17.4\",\"latest\":\"0.17.5\",\"state\":\"ready\",\"canDownload\":true}"));}
                return(200,JsonSerializer.SerializeToUtf8Bytes(new{current="0.17.4",latest="0.17.5",state=current,canDownload=true}));
            };
            var observed=new ObservedSession(new RemoteUpdateSession(root,"0.17.4",server.Port));
            using(var window=new Window(observed)) {await window.Started.Task;await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));await window.Click();await Until(()=>current=="downloading");oldRelease.TrySetResult();
                for(int i=0;i<20;i++){Require(await window.Primary()!="Apply & restart","late pre-command status cannot publish Ready");await Task.Delay(50);}
                await Until(()=>window.Primary().GetAwaiter().GetResult()=="Cancel download");Require(observed.Maximum==1,"one poll in flight");await window.Close();await window.Ended.Task;}
            File.WriteAllText(Path.Combine(root,"remote-observation.json"),JsonSerializer.Serialize(new { identityMutations=3,cancelledHttp=true,deadlineMs=elapsed.ElapsedMilliseconds,lateResponseAfterClose=true,lateGenerationRejected=true,maximumPolls=observed.Maximum,localRemoteProjectionMatched=true,languages=2,requests=server.Requests.ToArray() }));
        } finally {File.WriteAllText(layout.ServicePidPath,pid);File.WriteAllText(layout.WebPortPath,port);await StopHost(host,root);}
    }
    private static async Task<Process> StartHost(string root)
    {
        Directory.CreateDirectory(Path.Combine(root,"config"));
        string settings=Path.Combine(root,"config/settings.json");if(!File.Exists(settings))File.WriteAllText(settings,JsonSerializer.Serialize(new{WebPort=FreePort(),HostLocale="en-US",LightweightMode=true,OpenDesktopOnStartup=false,UpdateCheckEnabled=false}));
        var start=new ProcessStartInfo(Path.Combine(root,"NexusPipeline.exe"),"web") { UseShellExecute=false,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true };
        string exit=Path.Combine(root,".nxp","test-host.exit");if(File.Exists(exit))File.Delete(exit);
        start.Environment["NEXUS_SYSTEM_RUNTIME_NAME"]=Environment.GetEnvironmentVariable("NEXUS_SYSTEM_RUNTIME_NAME")??Path.GetFileName(root)+"-"+Environment.ProcessId;
        start.Environment["NEXUS_TEST_HOST_EXIT_FILE"]=exit;start.Environment["NEXUS_TEST_MODE"]="test-host";
        using(var document=JsonDocument.Parse(File.ReadAllBytes(settings)))if(document.RootElement.TryGetProperty("UpdateSourceUrl",out var url)&&url.GetString() is string source&&!string.IsNullOrWhiteSpace(source))start.Environment["NEXUS_UPDATE_URL"]=source;
        var process=Process.Start(start)!;_ = process.StandardOutput.ReadToEndAsync();_ = process.StandardError.ReadToEndAsync();
        var layout=new RuntimeStateLayout(root);await Until(()=>File.Exists(layout.WebPortPath)&&File.Exists(layout.ServicePidPath)&&!process.HasExited);
        await Until(()=>InstallerServiceHandoff.FindOwningServicePort(root,ApplicationPayload.ReadHostVersion(root)) is not null);return process;
    }
    private static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private static async Task StopHost(Process process,string root)
    {
        if(process.HasExited)return;Require(process.MainModule!.FileName==Path.Combine(root,"NexusPipeline.exe"),"owned Host image");File.WriteAllText(Path.Combine(root,".nxp","test-host.exit"),"exit");await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static partial Task NativeCases(Dictionary<string,string> options);
}
