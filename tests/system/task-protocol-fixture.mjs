import fs from "node:fs";
import path from "node:path";

export function installTaskProtocolFixture(runtimeDir) {
  const root = path.join(runtimeDir, "plugins", "TaskProtocolFixture");
  fs.mkdirSync(path.join(root, "data"), { recursive: true });
  const write = (name, data) => fs.writeFileSync(path.join(root, name), typeof data === "string" ? data : JSON.stringify(data), "utf8");
  write("plugin.json", {
    schemaVersion: 2, name: "task-protocol-fixture", artifactName: "TaskProtocolFixture", displayName: "Task fixture",
    version: "0.1.0", kind: "data-specialized", minHostVersion: "0.16.8", resolve: "data/resolve.json", judgeScript: "data/judge.js",
    taskProtocol: { version: "0.1.0", discoverScript: "data/discover.js", retryScript: "data/retry.js", readResources: [],
      localization: { defaultLocale: "en-US", messages: { "en-US": "data/i18n/en-US.json" } },
      configRules: [{ id: "fixture.default", required: true, criticality: "critical_when_applicable" }], environmentChecks: [] },
  });
  fs.mkdirSync(path.join(root, "data", "i18n"), { recursive: true });
  write("data/i18n/en-US.json", {});
  write("data/resolve.json", { inputs: [], require: [{ var: "main", file: "task-run.bat" }], paths: {
    mainExe: "{main}", args: "", configPath: "cfg/config.json", logPath: "",
  } });
  write("data/discover.js", `
    const resource = input.configResources[0].id;
    const config = nexus.readConfig(resource).document;
    console.log({protocolVersion:'0.1.0',type:'discovery',coverage:'complete',diagnostics:[],
      configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture.default',evaluation:'satisfied',severity:'info',executionEffect:'none',scope:{kind:'binding'},locations:[],actions:[]}]},
      selectionFields:config.tasks.map(t=>({resourceId:resource,selector:['tasks',{by:'id',value:t.id},'enabled'],purpose:'selection'})),
      tasks:config.tasks.map((t,i)=>({id:t.id,sourceKey:t.id,name:t.id,parentId:null,role:'business',enabled:t.enabled,
        order:i,countsAsUnit:true,requiredForParent:true,retryUnitId:t.id,retryRisk:'safe',dependencies:[],detection:'supported',configRef:resource}))});
  `);
  write("data/judge.js", `
    const end=input.logBatch.records.find(r=>r.text==='RUN END');
    console.log({protocolVersion:'0.1.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
      runBoundary:end?'ended':'open',boundaryEvidence:end?[{sourceId:end.sourceId,epoch:end.epoch,sequence:end.sequence,ruleId:'fixture.run.end'}]:[],diagnostics:[],observations:input.logBatch.records.filter(r=>/^TASK [ab] (OK|FAIL)$/.test(r.text)).map(r=>{
        const p=r.text.split(' ');return {id:r.sourceId+':'+r.epoch+':'+r.sequence,taskId:p[1],executionOrdinal:1,
        status:p[2]==='OK'?'succeeded':'failed',reasonCode:'fixture.task',evidence:[{sourceId:r.sourceId,epoch:r.epoch,sequence:r.sequence,ruleId:'fixture.task'}]};})});
  `);
  write("data/retry.js", `
    const id=input.configResources[0].id, current=nexus.readConfig(id);
    const included=input.originalPlan.tasks.filter(t=>t.enabled&&['failed','blocked'].includes(input.taskStates[t.id])).map(t=>t.id);
    console.log({protocolVersion:'0.1.0',type:'retry',decision:included.length?'selective':'stop',reasonCode:'retry.unfinished',
      includedTaskIds:included,prerequisiteTaskIds:[],expandedUnitIds:[],filePatches:included.length?[{resourceId:id,format:'json',expectedRevision:current.revision,
        operations:current.document.tasks.filter(t=>t.enabled!==included.includes(t.id)).map(t=>({selector:['tasks',{by:'id',value:t.id},'enabled'],
          expected:t.enabled,value:included.includes(t.id),purpose:'selection'}))}]:[]});
  `);
}

export function installTaskProtocolAdmissionFixture(runtimeDir) {
  const source = path.join(runtimeDir, "plugins", "TaskProtocolFixture");
  const root = path.join(runtimeDir, "plugins", "TaskProtocolAdmissionFixture");
  fs.cpSync(source, root, { recursive: true });
  const manifestPath = path.join(root, "plugin.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.name = "task-protocol-admission-fixture";
  manifest.artifactName = "TaskProtocolAdmissionFixture";
  manifest.taskProtocol.version = "0.1.0";
  manifest.taskProtocol.localization = {
    defaultLocale: "en-US", messages: { "en-US": "data/i18n/en-US.json" },
  };
  manifest.taskProtocol.configRules = [{ id: "fixture.block", required: true, criticality: "critical_when_applicable" }];
  manifest.taskProtocol.environmentChecks = [];
  fs.mkdirSync(path.join(root, "data", "i18n"), { recursive: true });
  fs.writeFileSync(path.join(root, "data", "i18n", "en-US.json"), "{}", "utf8");
  fs.writeFileSync(manifestPath, JSON.stringify(manifest), "utf8");
  for (const file of ["discover.js", "judge.js", "retry.js"]) {
    const target = path.join(root, "data", file);
    let sourceCode = fs.readFileSync(target, "utf8");
    if (file === "discover.js") sourceCode = sourceCode.replace("selectionFields:", `
      configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture.block',
        evaluation:config.blocked?'violated':'satisfied',severity:'info',
        executionEffect:config.blocked?'block':'none',scope:{kind:'binding'},
        locations:[],actions:[],reasonText:{kind:'literal',value:'Fixture retry admission blocked'}}]},
      selectionFields:`);
    fs.writeFileSync(target, sourceCode, "utf8");
  }
}
