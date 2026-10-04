import {controlManifest as readControlManifest,sourceFingerprint} from "./control-inputs.mjs";
import {validatePairCheckout} from "./ci-inputs.mjs";
import fs from "node:fs";
import path from "node:path";
import {execFileSync} from "node:child_process";
import {createHash} from "node:crypto";
import {coreUnits,allocateUnits,digest} from "./core-plan.mjs";
import {readRegistry,policyDigest,planForChanges,createScopePlan} from "./scope-plan.mjs";
const hash=bytes=>createHash("sha256").update(bytes).digest("hex");
const same=(a,b)=>JSON.stringify([...a].sort())===JSON.stringify([...b].sort());

export function loadBatch(root,args) {
  const options={};
  for(let i=0;i<args.length;i++) {
    const flag=args[i],value=args[++i];
    if(!["--plan","--batch","--partner-root"].includes(flag)||Object.hasOwn(options,flag)||!value||value.startsWith("--"))
      throw new Error("Usage: batch --plan <plan> --batch <control|batch-01..05> [--partner-root <fixed Plugins>]");
    options[flag]=value;
  }
  if(!options["--plan"]||!options["--batch"]) throw new Error("Missing batch input");
  const file=path.resolve(options["--plan"]);
  if(fs.lstatSync(file).isSymbolicLink()||fs.statSync(file).size>4*1024*1024) throw new Error("Unsafe plan file");
  const bytes=fs.readFileSync(file);policyDigest(bytes);
  const plan=JSON.parse(bytes);
  if(plan.schemaVersion!==2||plan.repository!=="Host"||plan.capacityStatus!=="PLANNED"||plan.estimatedTotalJobs>10||plan.batches.length>5)
    throw new Error("Invalid or CAPACITY_EXCEEDED batch plan");
  const policy=JSON.parse(fs.readFileSync(path.join(root,"tests/policy.json")));
  const {registry}=readRegistry(root);
  const actualManifest=readControlManifest(root);
  if(digest(actualManifest)!==digest(plan.controlManifest)||hash(JSON.stringify(actualManifest))!==plan.policyDigest) throw new Error("Control input fingerprint differs from plan");
  const partnerRoot=options["--partner-root"] ? path.resolve(options["--partner-root"]) : process.env.NEXUS_PARTNER_ROOT;
  validatePairCheckout(root,file,plan,partnerRoot);
  const partnerPolicy=partnerRoot ? JSON.parse(fs.readFileSync(path.join(partnerRoot,"tests/policy.json"))) : plan.partnerPolicySnapshot ?? null;
  if(plan.partnerPolicyDigest!== (partnerPolicy?hash(JSON.stringify(partnerPolicy)):null)) throw new Error("Partner policy differs from plan");
  const routing=planForChanges(root,plan.changes,registry,policy);
  if(!same(routing.selected.map(item=>item.id),plan.selected.map(item=>item.id))) throw new Error("Selected obligations differ from semantic plan");
  const identity={baseSha:plan.baseSha,headSha:plan.headSha,mergeBase:plan.mergeBase,testedSha:plan.testedSha,dirty:plan.dirty,changes:plan.changes,
    partnerSha:plan.partnerSha,...(plan.inputPair ? {inputPair:plan.inputPair} : {}),controlManifest:plan.controlManifest,partnerPolicyDigest:plan.partnerPolicyDigest};
  const expected=allocateUnits(coreUnits(plan.selected,registry,policy,partnerPolicy),identity,policy.ciBatchPolicy);
  if(expected.units.some(unit=>unit.kind==="partner-jint"&&!unit.expectedCaseIds.length)) throw new Error("Missing predeclared partner Jint cases");
  for(const field of ["units","batches","control","requiredObligations","capacityStatus","estimatedTotalJobs"])
    if(digest(expected[field])!==digest(plan[field])) throw new Error(`Altered batch plan: ${field}`);
  if(sourceFingerprint(root)!==plan.sourceFingerprint) throw new Error("Source bytes differ from scope plan");
  const sourceSha=execFileSync("git",["-C",root,"rev-parse","HEAD"],{encoding:"utf8"}).trim();
  if(sourceSha!==plan.testedSha) throw new Error("Tested source commit differs from plan");
  if(partnerRoot&&plan.partnerSha&&execFileSync("git",["-C",partnerRoot,"rev-parse","HEAD"],{encoding:"utf8"}).trim()!==plan.partnerSha) throw new Error("Partner commit differs from plan");
  if(process.env.CI) {
    if(plan.diagnosticSelection) throw new Error("Diagnostic selection cannot qualify Actions");
    if(plan.dirty||String(plan.runId)!==process.env.GITHUB_RUN_ID||String(plan.attempt)!==process.env.GITHUB_RUN_ATTEMPT) throw new Error("CI run/attempt/clean identity mismatch");
    const current=createScopePlan(root,{base:plan.baseSha,head:plan.headSha,inputPair:plan.inputPair,partnerSha:plan.partnerSha,partnerRoot});
    if(digest(current.changes)!==digest(plan.changes)) throw new Error("CI source diff differs from plan");
  }
  const batch=options["--batch"]==="control"?plan.control:plan.batches.find(item=>item.id===options["--batch"]);
  if(!batch?.units.length) throw new Error("Unknown or empty batch");
  if(batch.units.some(unit=>unit.kind==="partner-jint"&&!unit.partnerPlugins.length)) throw new Error("Missing fixed partner native obligations");
  return {plan,batch,partnerRoot,planDigest:hash(bytes)};
}

export function saveBatch(context,runRoot,workspace,units,exitCode,elapsedMs,cleanup) {
  const artifacts=[];
  const visit=directory=>{
    for(const entry of fs.readdirSync(directory,{withFileTypes:true})) {
      const file=path.join(directory,entry.name);
      if(entry.isSymbolicLink()) throw new Error("Linked batch evidence");
      if(entry.isDirectory()&&entry.name==="evidence") visit(file);
      else if(entry.isDirectory()&&directory!==runRoot) visit(file);
      else if(entry.isFile()&&entry.name!=="batch-report.json") {
        const bytes=fs.readFileSync(file);
        artifacts.push({path:path.relative(runRoot,file).replaceAll("\\","/"),sha256:hash(bytes),sizeBytes:bytes.length});
      }
    }
  };
  fs.mkdirSync(runRoot,{recursive:true});visit(runRoot);
  const report={schemaVersion:2,scope:"LOCAL_BATCH_RESULT",qualification:process.env.CI?"CI_PRODUCER_PENDING_AUDIT":"LOCAL_DIAGNOSTIC",
    batchId:context.batch.id,identity:{repository:"FlappiBakuse/NexusPipeline",prNumber:context.plan.prNumber,baseSha:context.plan.baseSha,
      headSha:context.plan.headSha,mergeBaseSha:context.plan.mergeBase,testedSha:context.plan.testedSha,runId:context.plan.runId,attempt:context.plan.attempt,
      inputMode:context.plan.inputMode??"default",inputPair:context.plan.inputPair??null,partnerSha:context.plan.partnerSha,partner:context.partnerSource??null,partnerFingerprint:context.partnerFingerprint??null,source:workspace?.source??null,sourceFingerprint:workspace?.sourceFingerprint??null,
      workingTreeDirty:workspace?.source.workingTreeDirty??null,toolchain:{...workspace?.toolchain,platform:process.platform,arch:process.arch,rid:"win-x64",buildModes:["production","test-host"]},
      toolchainFingerprint:hash(JSON.stringify({...workspace?.toolchain,platform:process.platform,arch:process.arch,rid:"win-x64",buildModes:["production","test-host"]}))},policyDigest:context.plan.policyDigest,planDigest:context.planDigest,
    status:exitCode?"FAIL":"PASS",exitCode,timing:{qualificationMs:150000,hardTimeoutMs:180000,processElapsedMs:elapsedMs,preparationElapsedMs:context.setupElapsedMs??0,completeJobMs:null},
    cleanupComplete:cleanup.cleanupComplete,units:units.map(unit=>({real:[],substituted:[],...unit})),artifacts};
  fs.writeFileSync(path.join(runRoot,"batch-report.json"),JSON.stringify(report,null,2)+"\n");
  return report;
}
