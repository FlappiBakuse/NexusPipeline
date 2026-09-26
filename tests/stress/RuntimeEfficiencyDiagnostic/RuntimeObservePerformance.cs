using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;

namespace NexusPipeline.StressDiagnostics;

internal static class RuntimeObservePerformance
{
    internal static async Task<int> Measure(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("--runtime-observe <new-owned-directory> <source-label>");
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new IOException("Runtime measurement needs a new owned directory");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".nxp-runtime-measurement-owned"), args[1]);
        var samples = new List<object>();
        using Process process = Process.GetCurrentProcess();
        for (int sample = 0; sample < 30; sample++)
        {
            string folder = Path.Combine(root, sample.ToString());
            Directory.CreateDirectory(folder);
            string config = Path.Combine(folder, "config.json");
            File.WriteAllText(config, "{\"tasks\":[{\"id\":\"a\",\"enabled\":true}]}");
            var protocol = new TaskProtocolDescriptor("0.1.0", """
              const id=input.configResources[0].id,config=nexus.readConfig(id).document;
              console.log({protocolVersion:'0.1.0',type:'discovery',coverage:'complete',diagnostics:[],
                configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture.default',evaluation:'satisfied',severity:'info',executionEffect:'none',scope:{kind:'binding'},locations:[],actions:[]}]},
                selectionFields:config.tasks.map(t=>({resourceId:id,selector:['tasks',{by:'id',value:t.id},'enabled'],purpose:'selection'})),
                tasks:config.tasks.map((t,i)=>({id:t.id,sourceKey:t.id,name:t.id,parentId:null,role:'business',enabled:t.enabled,order:i,countsAsUnit:true,requiredForParent:true,retryUnitId:t.id,retryRisk:'safe',dependencies:[],detection:'supported',configRef:id}))});
              """, """
              console.log({protocolVersion:'0.1.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
                observations:[],runBoundary:'unknown',boundaryEvidence:[],diagnostics:[],cursorState:{}});
              """, "", []) { ConfigRules = [new("fixture.default",true,"critical_when_applicable")] };
            var script = new ScriptInstance { Id="owned", PluginType="fictional", ConfigPath=config, RootPath=folder };
            var spec = new ResolvedScriptSpec(script,"1",new(true,"javascript","plugin-file","",""),"fixture") {TaskProtocol=protocol};
            var run = new TaskProtocolRun(spec,"run","user",Path.Combine(folder,"journal"));
            int publications=0, snapshots=0;
            run.Changed += _ => publications++;
            long allocated=GC.GetTotalAllocatedBytes(true);
            process.Refresh();
            TimeSpan cpu=process.TotalProcessorTime;
            long workingSetBefore=process.WorkingSet64;
            var wall=Stopwatch.StartNew();
            await run.BeginAsync(1,default);
            int beginPublications=publications;
            for(int tick=0;tick<100;tick++)
                if ((await run.ObserveAsync(false,default)).JudgeError is { } error) throw new InvalidDataException(error);
            int emptyPublications=publications-beginPublications;
            if ((await run.ObserveAsync(true,default)).JudgeError is { } finalError) throw new InvalidDataException(finalError);
            int finalPublications=publications-beginPublications-emptyPublications;
            var started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var judge=new SessionJudge(script);
            await using(var workers=new RuntimeWorkers("attempt",1,CancellationToken.None,"measurement","owned",judge,_=>{},generation=> {
                snapshots++;
                string input=JsonSerializer.Serialize(new { longLog = new string('x',100*1024) });
                return new JudgeSnapshot("attempt",1,generation,"",DateTime.Now,script,null,"",input,[]);
            },_=>{},_=>{},taskObserver:async (final,token)=>{
                started.TrySetResult(true); await release.Task.WaitAsync(token); return new JudgeScriptResult{Status="pending"};
            })) {
                if(!workers.QueueJudge(false))throw new InvalidOperationException("Initial judge not started");
                await started.Task;
                for(int tick=0;tick<600;tick++) if(workers.QueueJudge(false))throw new InvalidOperationException("Busy judge accepted overlap");
                release.SetResult(true);
            }
            wall.Stop();process.Refresh();
            samples.Add(new {sample,wallMs=wall.Elapsed.TotalMilliseconds,cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds,
                allocatedBytes=GC.GetTotalAllocatedBytes(true)-allocated,workingSetBefore,workingSetAfter=process.WorkingSet64,
                observeCalls=101,emptyPublications,finalPublications,busyQueueAttempts=600,snapshots});
            File.WriteAllText(Path.Combine(root,"runtime-observe-performance.json"),JsonSerializer.Serialize(new{
                schemaVersion=1,sourceLabel=args[1],samples,failures=0,
                scope="Actual Begin + 100 empty Jint Observe + final + busy RuntimeWorkers with 100 KiB input. Long file log measured separately; no game or simulated wall-clock claim."
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        Console.WriteLine($"[runtime-observe] 30 samples: {root}");
        return 0;
    }
}
