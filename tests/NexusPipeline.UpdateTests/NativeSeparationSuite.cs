using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Maintenance;
using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Updater;

internal static partial class SeparationSuite
{
    private static string NativeEngineHash="";
    private static partial async Task NativeCases(Dictionary<string,string> options)
    {
        string root=Path.Combine(Output,"native-app"),scope=Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE")!;
        using var server=new Loopback();bool policyFails=false;var streaming=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);release.TrySetResult();
        string package=options["--package"];
        var assets=Directory.GetFiles(Path.GetDirectoryName(package)!,"NexusPipeline-v0.17.5-win-x64-*").Order().Select((file,index)=>new { id=17501+index,state="uploaded",name=Path.GetFileName(file),size=new FileInfo(file).Length,digest="sha256:"+Hash(file),browser_download_url=$"http://127.0.0.1:{server.Port}/"+Path.GetFileName(file) }).ToArray();
        Require(assets.Length==3,"actual three-asset protocol fixture");
        string source=$"http://127.0.0.1:{server.Port}/releases";
        server.Response=path=>Task.FromResult(path.EndsWith("update-policy.json")
            ? (policyFails?503:200,Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"repository\":\"FlappiBakuse/NexusPipeline\",\"barriers\":[]}"))
            : (200,JsonSerializer.SerializeToUtf8Bytes(new[]{new { id=175,tag_name="v0.17.5",draft=false,prerelease=true,body="controlled release notes",assets }})));
        server.StreamResponse=async(path,stream)=> { if(!path.EndsWith(".zip"))return false;var asset=assets.Single(a=>"/"+a.name==path);await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {asset.size}\r\nConnection: close\r\n\r\n"));
            await using var file=File.OpenRead(Path.Combine(Path.GetDirectoryName(package)!,asset.name));byte[] first=new byte[65536];int read=await file.ReadAsync(first);await stream.WriteAsync(first.AsMemory(0,read));streaming.TrySetResult();await release.Task;await file.CopyToAsync(stream);return true; };
        string setup=options["--native-setup"],nativeSnapshot=Path.Combine(Output,"native-log-inputs");
        await Case("E05-U07-native-cli-install-and-same-version-repair",async()=> {
            await Install(setup,root,"fresh");var owner=InstallationOwnership.Read(root,true)!;Require(owner.SchemaVersion==2&&owner.Version=="0.17.4","native setup registered dedicated tools");NativeEngineHash=owner.InstallerTools!.NativeEngine.Sha256;
            SetSettings(root,source);Directory.CreateDirectory(Path.Combine(root,"data"));File.WriteAllBytes(Path.Combine(root,"data/user.bin"),[0,255,13,10,42]);string user=Hash(Path.Combine(root,"data/user.bin"));
            Directory.CreateDirectory(nativeSnapshot);foreach(string file in Directory.GetFiles(root,"unins*"))File.Copy(file,Path.Combine(nativeSnapshot,Path.GetFileName(file)));
            await Install(setup,root,"same-version");await StopCandidate(root);Require(InstallationOwnership.Read(root,true)!.InstanceId==owner.InstanceId,"same version retains instance ID");Require(Hash(Path.Combine(root,"data/user.bin"))==user,"same version retains user bytes");
            string metadata=Path.Combine(Tools,"installer-metadata/nxp-metadata-helper.exe");Require(await Cli(metadata,["installer-state","require-stopped","--root",root])==0,"require-stopped CLI");Require(await Cli(metadata,["installer-state","metadata-recover","--root",root])==0,"recover CLI");Require(await Cli(metadata,["installer-state","unknown","--root",root])==1,"invalid metadata action propagates");Require(await Cli(metadata,["web"])==2,"no Host service command");
        });
        await Case("P04-native-public-cancel",async()=> {
            var before=Inventory(root);using var process=Launch(Path.Combine(root,InstallationOwnership.UninstallEntry),[],root);await process.WaitForExitAsync();Require(process.ExitCode==0,"public launcher releases native chain");
            await Dialog(root,"configuration and data",2);await Until(()=>!NativeAlive(root));Require(Inventory(root).SequenceEqual(before),"cancel preserves exact application/data bytes");Require(InstallationOwnership.Read(root,true)!.State=="active","cancel preserves active registration");
        });
        await Case("P04-native-apps-keep-and-reinstall",async()=> {
            var owner=InstallationOwnership.Read(root,true)!;string user=Hash(Path.Combine(root,"data/user.bin"));using var process=Launch(owner.InstallerTools!.NativeEngine.Path,["/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/LOG="+Path.Combine(Output,"keep-uninstall.log")],root);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));Require(process.ExitCode==0,"native Apps silent keep exit "+process.ExitCode);
            await Until(()=>!File.Exists(Path.Combine(root,"NexusPipeline.exe")));Require(Hash(Path.Combine(root,"data/user.bin"))==user,"silent uninstall defaults keep data");Require(InstallationOwnership.Read(root)!.State=="retained-data","retained state recorded");
            await Install(setup,root,"retained-reinstall",interactive:true);Require(InstallationOwnership.Read(root,true)!.InstanceId==owner.InstanceId,"reinstall keeps instance ID");Require(Hash(Path.Combine(root,"data/user.bin"))==user,"reinstall retains bytes");
        });
        await Case("P04-native-public-delete-data",async()=> {
            using var process=Launch(Path.Combine(root,InstallationOwnership.UninstallEntry),[],root);await process.WaitForExitAsync();await Dialog(root,"configuration and data",7);
            await Dialog(root,"remove",6);await Until(()=>!File.Exists(Path.Combine(root,"NexusPipeline.exe")),60000);await Dialog(root,"successfully removed",1);await Until(()=>!NativeAlive(root));
            Require(!Directory.Exists(Path.Combine(root,"data")),"explicit deletion removes data");Require(InstallationOwnership.Read(root) is null,"explicit deletion removes protected registration");
        });
        await Case("P06-P08-authentic-old-reader-and-native-first-hop",async()=> {
            string official=options["--official-baseline"],host=Path.Combine(official,"NexusPipeline.exe");
            var read=typeof(BundleResourceReader).GetMethod("ReadAssembly",BindingFlags.Static|BindingFlags.NonPublic)!;
            byte[] AssemblyBytes(string name)=>(byte[])read.Invoke(null,[host,name,null])!;
            var context=new AssemblyLoadContext("official-0.17.3-reader",true);
            context.Resolving+=(_,name)=> { try{return context.LoadFromStream(new MemoryStream(AssemblyBytes(name.Name+".dll")));}catch(TargetInvocationException){return null;} };
            var original=context.LoadFromStream(new MemoryStream(AssemblyBytes("NexusPipeline.dll")));
            var validate=original.GetType("NexusPipeline.Modules.Updates.ApplicationPayload")!.GetMethod("Validate",BindingFlags.Static|BindingFlags.NonPublic)!;
            Require(validate.Invoke(null,[official,"0.17.3",false]) is not null,"authentic original reader validates official payload");
            try {validate.Invoke(null,[ApplicationRoot,"0.17.4",false]);throw new InvalidDataException("Original reader unexpectedly accepted schema2");}catch(TargetInvocationException error)when(error.InnerException is InvalidDataException){}
            context.Unload();
            Directory.CreateDirectory(root);foreach(string file in Directory.GetFiles(official,"*",SearchOption.AllDirectories)){string destination=Path.Combine(root,Path.GetRelativePath(official,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,true);}foreach(string file in Directory.GetFiles(nativeSnapshot))File.Copy(file,Path.Combine(root,Path.GetFileName(file)),true);
            string manifest=Path.Combine(Output,"official-schema1-inventory.json");var inventory=ApplicationPayload.Files(official).Select(f=>new InstalledPayloadFile(f.Path,f.Sha256)).Append(new(ApplicationPayload.ManifestPath,Hash(Path.Combine(root,ApplicationPayload.ManifestPath)))).OrderBy(f=>f.Path,StringComparer.Ordinal).ToArray();
            File.WriteAllText(manifest,JsonSerializer.Serialize(inventory));InstallationOwnership.RegisterOwnedInventoryForTest(root,"0.17.3",manifest,inventory);
            File.Copy(host,Path.Combine(InstallationOwnership.ManagerDirectory,"nexus-installer-helper.exe"),true);
            using(var key=Registry.CurrentUser.CreateSubKey(InstallationOwnership.CurrentUninstallRegistryPath)){key.SetValue("UninstallString","\""+Path.Combine(root,"unins000.exe")+"\"");key.SetValue("DisplayVersion","0.17.3");}
            var before=InstallationOwnership.Read(root,true)!;SetSettings(root,source);Directory.CreateDirectory(Path.Combine(root,"data"));File.WriteAllBytes(Path.Combine(root,"data/user.bin"),[0,255,42]);
            await Install(setup,root,"official-schema1-first-hop");await StopCandidate(root);var after=InstallationOwnership.Read(root,true)!;
            Require(after.SchemaVersion==2&&after.Version=="0.17.4"&&after.InstanceId==before.InstanceId,"native first hop migrates schema1 preserving instance");Require(!File.Exists(Path.Combine(root,ApplicationPayload.LegacyUpdateEntry)),"legacy space entry retired");Require(File.ReadAllBytes(Path.Combine(root,"data/user.bin")).SequenceEqual(new byte[]{0,255,42}),"first-hop data retained");
            File.WriteAllText(Path.Combine(Output,"official-reader.json"),JsonSerializer.Serialize(new { officialHostSha256=Hash(host),originalReaderExecuted=true,originalMainExecuted=false,selectedFirstHop="native Setup",schema1Source="isolated sealed identity with authenticated official payload",targetVersion="0.17.4",productionUacTested=false }));
        });
        await Case("E07-installed-host-version-window",async()=> {
            string versionRoot=Path.Combine(Output,"version-source");CopyApplication(options["--official-baseline"],versionRoot);SetSettings(versionRoot,source);
            File.Copy(Path.Combine(ApplicationRoot,UpdateRecipe.UpdateEntry),Path.Combine(versionRoot,UpdateRecipe.UpdateEntry));
            using var window=new Window(()=>UpdateSessions.Open(versionRoot));await window.Started.Task;await Until(()=>window.On(f=>Controls(f).OfType<System.Windows.Forms.Label>().Any(l=>l.Text.Contains("0.17.3"))).GetAwaiter().GetResult());
            Require(ToolBuildIdentity.Read(Path.Combine(versionRoot,UpdateRecipe.UpdateEntry),"updater").ProductVersion=="0.17.4","tool version differs from installed official Host");
            await window.Close();await window.Ended.Task;
            Require(ApplicationPayload.ReadHostVersion(versionRoot)=="0.17.3","installed Host source version unaffected by tool version");
            File.AppendAllText(Path.Combine(versionRoot,"README.md"),"unknown source bytes");
            try{UpdateSessions.Open(versionRoot);throw new InvalidDataException("Wrong source bytes accepted");}catch(IOException){}
        });
        await Case("W05-local-window-close-success-and-cleanup-conflict",async()=> {
            foreach(bool conflict in new[]{false,true}) {
                string local=Path.Combine(Output,"local-close-"+conflict);CopyApplication(ApplicationRoot,local);SetSettings(local,source);
                release=new(TaskCreationOptions.RunContinuationsAsynchronously);streaming=new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var window=new Window(()=>UpdateSessions.Open(local));await window.Started.Task;
                await window.On(f=>{Controls(f).OfType<System.Windows.Forms.Button>().First().PerformClick();return true;});await Until(()=>window.Primary().GetAwaiter().GetResult()=="Download"&&window.PrimaryEnabled().GetAwaiter().GetResult());await window.Click();await streaming.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Until(()=>Directory.Exists(Path.Combine(local,".nxp-update"))&&Directory.GetFiles(Path.Combine(local,".nxp-update"),"*.zip*",SearchOption.AllDirectories).Any(file=>new FileInfo(file).Length>0));
                string? unknown=null;if(conflict){unknown=Path.Combine(local,".nxp-update","unknown.bin");File.WriteAllBytes(unknown,[0,255,42]);}
                await window.Close();if(conflict)await DialogForProcess(Environment.ProcessId,"NexusPipeline.update",1);
                await window.Ended.Task.WaitAsync(TimeSpan.FromSeconds(15));release.TrySetResult();
                if(unknown is not null)Require(File.ReadAllBytes(unknown).SequenceEqual(new byte[]{0,255,42}),"local close preserves and reports unknown cleanup bytes");
                else Require(!Directory.Exists(Path.Combine(local,".nxp-update"))||!Directory.EnumerateFiles(Path.Combine(local,".nxp-update"),"*.zip*",SearchOption.AllDirectories).Any(),"local close drains download cleanup");
            }
        });
        await Case("W06-real-host-download-remote-close-and-explicit-cancel",async()=> {
            string remoteRoot=Path.Combine(Output,"host-download");CopyApplication(ApplicationRoot,remoteRoot);SetSettings(remoteRoot,source);using var host=await StartHost(remoteRoot);
            try {
                var port=int.Parse(File.ReadAllText(new RuntimeStateLayout(remoteRoot).WebPortPath));
                using var session=new RemoteUpdateSession(remoteRoot,"0.17.4",port);await session.CommandAsync("check",default);
                release=new(TaskCreationOptions.RunContinuationsAsynchronously);streaming=new(TaskCreationOptions.RunContinuationsAsynchronously);await session.CommandAsync("download",default);await streaming.Task;
                using(var window=new Window(new RemoteUpdateSession(remoteRoot,"0.17.4",port))) {await window.Started.Task;await Until(()=>window.Primary().GetAwaiter().GetResult()=="Cancel download");await window.Close();await window.Ended.Task;}
                Require(!host.HasExited&&(await session.ReadStatusAsync(default)).State=="downloading","closing remote window leaves Host download alive");
                using(var window=new Window(new RemoteUpdateSession(remoteRoot,"0.17.4",port))) {await window.Started.Task;await Until(()=>window.Primary().GetAwaiter().GetResult()=="Cancel download");await window.Click();await Until(()=>session.ReadStatusAsync(default).GetAwaiter().GetResult().State=="idle");await window.Close();await window.Ended.Task;}
                release.TrySetResult();Require(!host.HasExited,"explicit download cancel retains Host");
            }finally{release.TrySetResult();await StopHost(host,remoteRoot);}
        });
        foreach(string mode in new[]{"public-missing","all-missing","portable-missing","tampered"})await Case("E08-P05-W07-update-"+mode,async()=> {
            bool installed=mode is "public-missing" or "all-missing";string app=installed?root:Path.Combine(Output,mode);if(!installed){CopyApplication(ApplicationRoot,app);SetSettings(app,source);}else if(InstallationOwnership.Read(root,true)!.Version!="0.17.4")throw new InvalidDataException("Native source version changed");
            SetSettings(app,source);using var host=await StartHost(app);var owner=installed?InstallationOwnership.Read(app,true):null;
            string? userHash=installed?Hash(Path.Combine(app,"data/user.bin")):null;
            if(mode=="public-missing")File.Delete(owner!.InstallerTools!.PublicEntry.Path);
            if(mode=="all-missing"){File.Delete(owner!.InstallerTools!.PublicEntry.Path);File.Delete(owner.InstallerTools.Worker.Path);}
            File.Delete(Path.Combine(app,UpdateRecipe.UpdateEntry));if(mode=="tampered")File.WriteAllText(Path.Combine(app,UpdateRecipe.UpdateEntry),"unknown updater bytes");
            try {
                int port=int.Parse(File.ReadAllText(new RuntimeStateLayout(app).WebPortPath));using var session=new RemoteUpdateSession(app,"0.17.4",port);await session.CommandAsync("check",default);release=new(TaskCreationOptions.RunContinuationsAsynchronously);release.TrySetResult();await session.CommandAsync("download",default);
                await Until(()=>session.ReadStatusAsync(default).GetAwaiter().GetResult().State is "ready" or "idle",90000);
                if(mode=="tampered"){Require((await session.ReadStatusAsync(default)).State=="idle","tampered helper rejected");Require(File.ReadAllText(Path.Combine(app,UpdateRecipe.UpdateEntry))=="unknown updater bytes","tampered helper preserved");return;}
                Require((await session.ReadStatusAsync(default)).State=="ready","real download validation completed");
                string receipts=Path.Combine(app,".nxp/state/updates");var previousReceipts=Directory.Exists(receipts)?Directory.GetFiles(receipts,"*.result.json").ToHashSet(StringComparer.OrdinalIgnoreCase):[];
                if(mode=="portable-missing") { policyFails=true;using var window=new Window(new RemoteUpdateSession(app,"0.17.4",port));await window.Started.Task;await Until(()=>window.Primary().GetAwaiter().GetResult()=="Apply & restart");await window.Click();await Until(()=>window.Message().GetAwaiter().GetResult().Contains("policy-unavailable"));Require(!window.Ended.Task.IsCompleted,"rejection leaves readable window");policyFails=false;await window.Click();await window.Ended.Task; }
                else await session.CommandAsync("apply",default);
                await Until(()=>!File.Exists(Path.Combine(app,".nxp-update/task.json"))&&File.Exists(new RuntimeStateLayout(app).ServicePidPath)&&ApplicationPayload.ReadHostVersion(app)=="0.17.5",120000);
                string receipt=Directory.GetFiles(receipts,"*.result.json").Except(previousReceipts,StringComparer.OrdinalIgnoreCase).Single();var result=JsonNode.Parse(File.ReadAllText(receipt))!;Require(result["Succeeded"]!.GetValue<bool>()&&result["Version"]!.GetValue<string>()=="0.17.5","independent worker committed exact target");File.Copy(receipt,Path.Combine(Output,mode+"-result.json"));
                if(installed){var updated=InstallationOwnership.Read(app,true)!;Require(updated.Version=="0.17.5","installed ownership updated");Require(Hash(Path.Combine(app,"data/user.bin"))==userHash,"installed update preserves user bytes");if(mode=="public-missing")Require(File.Exists(updated.InstallerTools!.PublicEntry.Path),"trusted worker repairs public entry");else {Require(InstallationOwnership.InspectInstallerTools(updated)=="installer.uninstall_requires_setup","both missing permits main update and reports Setup repair");int currentPort=int.Parse(File.ReadAllText(new RuntimeStateLayout(app).WebPortPath));using var currentSession=new RemoteUpdateSession(app,"0.17.5",currentPort);Require((await currentSession.ReadStatusAsync(default)).Message=="installer.uninstall_requires_setup","actual updated Host reports Setup repair warning");}}
                else Require(!File.Exists(Path.Combine(app,InstallationOwnership.UninstallEntry)),"portable has no uninstall tool");
                await StopCandidate(app);
            }finally {policyFails=false;await StopHost(host,app);await StopCandidate(app);}
            if(installed){CopyApplication(ApplicationRoot,app);var previous=owner!;string seal=InstallationOwnership.Seal(previous);File.WriteAllText(Path.Combine(InstallationOwnership.ManagerDirectory,"identity.protected"),seal);using var key=Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentRegistryPath,true);key!.SetValue("IdentityDigest",Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seal))));File.Copy(Path.Combine(Tools,"uninstaller/NexusPipeline.uninstall.exe"),previous.InstallerTools!.PublicEntry.Path,true);File.Copy(previous.InstallerTools.PublicEntry.Path,previous.InstallerTools.Worker.Path,true);}
        });
        using(var cleanup=Launch(Path.Combine(root,InstallationOwnership.UninstallEntry),[],root)){await cleanup.WaitForExitAsync();}
        await Dialog(root,"configuration and data",7);await Dialog(root,"remove",6);await Until(()=>!File.Exists(Path.Combine(root,"NexusPipeline.exe")),60000);await Dialog(root,"successfully removed",1);await Until(()=>!NativeAlive(root));
        Require(Registry.CurrentUser.OpenSubKey(InstallationOwnership.CurrentUninstallRegistryPath) is null,"native test Apps entry removed");
        Require(InstallationOwnership.Read(root) is null,"native test ownership removed");
    }
    private static void SetSettings(string root,string source) {Directory.CreateDirectory(Path.Combine(root,"config"));File.WriteAllText(Path.Combine(root,"config/settings.json"),JsonSerializer.Serialize(new { WebPort=FreePort(),HostLocale="en-US",LightweightMode=true,OpenDesktopOnStartup=false,UpdateCheckEnabled=false,UpdateSourceUrl=source,UpdateChannel="prerelease" }));}
    private static Process Launch(string executable,string[] args,string root) { var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=root};foreach(string arg in args)start.ArgumentList.Add(arg);start.Environment["NEXUS_TEST_HOST_EXIT_FILE"]=Path.Combine(root,".nxp/test-host.exit");return Process.Start(start)!; }
    private static async Task Install(string setup,string root,string label,bool interactive=false) {
        Directory.CreateDirectory(root);if(File.Exists(Path.Combine(root,".nxp/test-host.exit")))File.Delete(Path.Combine(root,".nxp/test-host.exit"));
        string[] common=["/SP-","/NORESTART","/LANG=en","/TASKS=","/DIR="+root,"/LOG="+Path.Combine(Output,label+"-setup.log")];
        using var process=Launch(setup,interactive?common:["/VERYSILENT","/SUPPRESSMSGBOXES",..common],root);
        bool confirmed=false;
        if(interactive){var timer=Stopwatch.StartNew();string temporary=Path.Combine(Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT")!,"tmp");
            while(!process.HasExited){Require(timer.Elapsed<TimeSpan.FromMinutes(4),"interactive Setup protocol deadline");
                foreach(var candidate in Process.GetProcesses())using(candidate)try{
                    string? image=candidate.MainModule?.FileName;
                    if(image is null||!image.StartsWith(temporary+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||ParentPid(candidate)!=process.Id)continue;
                    foreach(var window in Dialogs(candidate.Id)){
                        if(!IsWindowEnabled(window))continue;IntPtr button=IntPtr.Zero;string content=Content(window);
                        if(content.Contains("retained",StringComparison.OrdinalIgnoreCase)&&Choice(window,6)!=IntPtr.Zero){button=Choice(window,6);confirmed=true;}
                        else foreach(int action in new[]{9,8,10}){button=Choice(window,action);if(button!=IntPtr.Zero)break;}
                        if(button!=IntPtr.Zero){GetWindowThreadProcessId(button,out uint pid);Require(pid==candidate.Id,"owned native Setup button");Require(PostMessage(button,0xf5,IntPtr.Zero,IntPtr.Zero),"native Setup click posted");break;}
                    }
                }catch(Exception error)when(error is System.ComponentModel.Win32Exception or InvalidOperationException){}
                await Task.Delay(100);
            }
            Require(confirmed,"retained-data reinstall requires actual confirmation");
        }else await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
        Require(process.ExitCode==0,"native Setup "+label+" exit "+process.ExitCode);
    }
    private static async Task<int> Cli(string executable,string[] args) { using var process=Launch(executable,args,Output);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));return process.ExitCode; }
    private static string[] Inventory(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(root,p)+":"+Hash(p)).Order().ToArray();
    private static async Task StopCandidate(string root) { var layout=new RuntimeStateLayout(root);if(!File.Exists(layout.ServicePidPath))return;int pid=int.Parse(File.ReadAllText(layout.ServicePidPath));try{using var process=Process.GetProcessById(pid);await StopHost(process,root);}catch(ArgumentException){} }
    private static bool NativeImage(string image,string root)=>NativeEngineHash.Length==64&&(image.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(image).StartsWith("unins",StringComparison.OrdinalIgnoreCase)||image.StartsWith(Path.Combine(Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT")!,"tmp")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(image)=="_unins.tmp")&&Hash(image)==NativeEngineHash;
    private static bool NativeAlive(string root)=>Process.GetProcesses().Any(p=> {using(p)try{return !p.HasExited&&p.MainModule?.FileName is string image&&NativeImage(image,root);}catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException){return false;} });
    private static async Task Dialog(string root,string title,int choice) {IntPtr button=IntPtr.Zero;await Until(()=> {foreach(var process in Process.GetProcesses()) { using(process)try {string? image=process.MainModule?.FileName;if(image is not null&&NativeImage(image,root)) {var windows=Dialogs(process.Id);foreach(var error in windows.Where(h=>Content(h).Contains("could not be verified",StringComparison.OrdinalIgnoreCase))){string diagnostic=CaptureNativeProbe();SendMessage(Choice(error,1),0xf5,IntPtr.Zero,IntPtr.Zero);throw new InvalidDataException("Native ownership probe rejected: "+diagnostic);}var window=windows.FirstOrDefault(h=>Content(h).Contains(title,StringComparison.OrdinalIgnoreCase)&&Choice(h,choice)!=IntPtr.Zero);if(window!=IntPtr.Zero){button=Choice(window,choice);return true;}}}catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException&&e is not InvalidDataException){} }return false;});SendMessage(button,0xf5,IntPtr.Zero,IntPtr.Zero);}
    private static string CaptureNativeProbe(){string temporary=Path.Combine(Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT")!,"tmp"),destination=Path.Combine(Output,"native-probe-failure-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(destination);foreach(string directory in Directory.GetDirectories(temporary,"is-*.tmp")){PayloadPathSafety.RequireLinkFree(directory);foreach(string name in new[]{"nxp-instance-check.ps1","nxp-instance-check.result","nxp-instance-check.result.failure"}){string source=Path.Combine(directory,name);if(File.Exists(source))File.Copy(source,Path.Combine(destination,name));}}return Directory.GetFiles(destination,"*.failure").FirstOrDefault() is string failure?File.ReadAllText(failure):destination;}
    private static async Task DialogForProcess(int pid,string title,int choice) {IntPtr button=IntPtr.Zero;await Until(()=> {var window=Dialogs(pid).FirstOrDefault(h=>Title(h)==title&&Choice(h,choice)!=IntPtr.Zero);if(window!=IntPtr.Zero)button=Choice(window,choice);return button!=IntPtr.Zero;});SendMessage(button,0xf5,IntPtr.Zero,IntPtr.Zero);}
    private static List<IntPtr> Dialogs(int pid) {var result=new List<IntPtr>();EnumWindows((window,_)=> {GetWindowThreadProcessId(window,out uint id);if(id==pid&&IsWindowVisible(window))result.Add(window);return true;},IntPtr.Zero);return result;}
    private static string Title(IntPtr window){var text=new StringBuilder(1024);GetWindowText(window,text,text.Capacity);return text.ToString();}
    private static string Content(IntPtr window){var text=new StringBuilder(Title(window));EnumChildWindows(window,(child,_)=>{text.Append(' ').Append(Title(child));return true;},IntPtr.Zero);return text.ToString();}
    private static IntPtr Choice(IntPtr window,int choice){IntPtr result=IntPtr.Zero;EnumChildWindows(window,(child,_)=>{var kind=new StringBuilder(256);GetClassName(child,kind,kind.Capacity);if(!kind.ToString().EndsWith("Button",StringComparison.OrdinalIgnoreCase)||!IsWindowVisible(child)||!IsWindowEnabled(child))return true;string text=Title(child).Replace("&","").Trim();string[] captions=choice switch{1=>["OK","确定"],2=>["Cancel","取消"],6=>["Yes","是"],7=>["No","否"],8=>["Next","下一步"],9=>["Install","安装"],10=>["Finish","完成"],_=>[]};if(captions.Any(c=>text.StartsWith(c,StringComparison.OrdinalIgnoreCase)))result=child;return true;},IntPtr.Zero);return result;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessBasicInfo{internal IntPtr ExitStatus,Peb,Affinity,Priority,Pid,Parent;}
    private static int ParentPid(Process process)=>NtQueryInformationProcess(process.Handle,0,out ProcessBasicInfo information,Marshal.SizeOf<ProcessBasicInfo>(),out _)==0?information.Parent.ToInt32():-1;
    private delegate bool EnumProc(IntPtr window,IntPtr state);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr state);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr state);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    [DllImport("ntdll.dll")]private static extern int NtQueryInformationProcess(IntPtr process,int informationClass,out ProcessBasicInfo information,int length,out int returned);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
}
