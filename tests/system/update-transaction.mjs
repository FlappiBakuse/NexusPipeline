import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { createHash, randomUUID } from "node:crypto";
import { resolveTestRunRoot } from "../support/test-runtime.mjs";
import { readProcessIdentity } from "../support/windows-process.mjs";

process.env.NEXUS_SYSTEM_RUNTIME_NAME = "update-runtime";
const root = resolveTestRunRoot(path.resolve(import.meta.dirname, "../.."), process.env.NEXUS_TEST_RUN_ID);
process.env.NEXUS_TEST_HOST_EXIT_FILE = path.join(root, "update-runtime", ".nxp", "test-host.exit");
const runtime = await import("./runtime-helper.mjs");
const hash = file => createHash("sha256").update(fs.readFileSync(file)).digest("hex");
const readJson = file => JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/u, ""));

function receivedPayload(directory) {
  const manifestPath=path.join(directory,"resources/payload-manifest.json");
  const manifest=readJson(manifestPath);
  return {ManifestSha256:hash(manifestPath),BuildId:manifest.buildId,FrontendHash:manifest.frontendHash,Generation:manifest.installationGeneration};
}
function preserveFailedTransaction(label,journal) {
  if(!fs.existsSync(journal))return;
  const directory=path.join(root,label+'-failure');
  fs.mkdirSync(directory,{recursive:true});
  for(const relative of ['.nxp-update/task.json','.nxp/state/updates','logs']) {
    const source=path.join(runtime.runtimeDir,relative);
    if(fs.existsSync(source))fs.cpSync(source,path.join(directory,relative.replaceAll('/','-')),{recursive:true});
  }
}
function stageApplication(staging, readme) {
  fs.mkdirSync(staging,{recursive:true});
  for(const asset of ["NexusPipeline.exe","README.md","resources"]) fs.cpSync(path.join(runtime.updateReleaseDirectory(),asset),path.join(staging,asset),{recursive:true});
  fs.writeFileSync(path.join(staging,"README.md"),readme);
  const file=path.join(staging,"resources/payload-manifest.json"), manifest=readJson(file);
  const entry=manifest.files.find(item=>item.path==="README.md");
  entry.sha256=hash(path.join(staging,"README.md"));entry.sizeBytes=Buffer.byteLength(readme);
  fs.writeFileSync(file,JSON.stringify(manifest));
  return receivedPayload(staging);
}
function writeJournal(file,version,staging,payload) {
  const entries=[];
  function capture(directory,prefix=''){
    for(const entry of fs.readdirSync(directory,{withFileTypes:true})){
      const relative=prefix+entry.name,absolute=path.join(directory,entry.name);
      assert.equal(entry.isSymbolicLink(),false);
      entries.push({Path:relative,IsDirectory:entry.isDirectory(),SizeBytes:entry.isDirectory()?0:fs.statSync(absolute).size,Sha256:entry.isDirectory()?null:hash(absolute)});
      if(entry.isDirectory())capture(absolute,relative+'/');
    }
  }
  capture(staging);entries.sort((left,right)=>left.Path<right.Path?-1:left.Path>right.Path?1:0);
  fs.writeFileSync(file,JSON.stringify({Mode:"apply",Version:version,StagedDir:staging,Phase:"ApplyRequested",CreatedAt:new Date().toISOString(),
    PayloadSchemaVersion:1,TargetPayload:payload,PreviousPayload:null,PackageSha256:"a".repeat(64),PackageSource:"installer",DesktopStopped:true,SwappedAssetCount:0,
    JournalSchemaVersion:1,StagingInventory:{Entries:entries},BackupInventory:null,WorkerSha256:hash(runtime.runtimeExe),PackageChecksumSha256:null,Preservation:null,
    WorkerPath:null,WorkerLaunchPending:false,DesktopResumeIntent:{SchemaVersion:1,Mode:'background'},TransactionId:randomUUID().replaceAll('-',''),TargetImageHash:hash(path.join(staging,'NexusPipeline.exe'))}));
}

test("current update worker commits only after actual services are ready and preserves user files", async () => {
  await runtime.prepareRuntime();
  const settings = path.join(runtime.runtimeDir, "config/settings.json");
  fs.writeFileSync(settings, JSON.stringify({ ...JSON.parse(fs.readFileSync(settings)), UpdateCheckEnabled: false }));
  let version;
  runtime.startRuntime();
  try {
    await runtime.waitForService();
    version = (await (await runtime.api("GET", "api/status")).json()).version;
    assert.match(version, /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:beta|rc)\.(?:0|[1-9]\d*))?$/);
  } finally { await runtime.stopRuntime(); }
  const user = path.join(runtime.runtimeDir, "data", "preserved.bin");
  fs.mkdirSync(path.dirname(user), { recursive: true });
  fs.writeFileSync(user, Buffer.from([0, 255, 13, 10, 42]));
  const original = hash(user), beforeImage = hash(runtime.runtimeExe);
  assert.equal(version,'0.17.0');version='0.17.1';
  assert.equal(fs.readdirSync(path.join(runtime.runtimeDir,'resources/desktop/locales')).length,55);
  const targetImage=hash(path.join(runtime.updateReleaseDirectory(),'NexusPipeline.exe'));
  assert.notEqual(targetImage,beforeImage);
  const staging = path.join(runtime.runtimeDir, `.nxp-update/staging/${version}.g1`);
  const targetPayload = stageApplication(staging, "current update readme");
  const journal = path.join(runtime.runtimeDir, ".nxp-update/task.json");
  writeJournal(journal, version, staging, targetPayload);
  const plan = path.join(root, "update-policy-plan.json");
  fs.writeFileSync(plan, JSON.stringify({ RunId: runtime.runId, Exchanges: [{ Id: "fresh-worker-policy", Method: "GET",
    Url: "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json", Status: 200,
    BodyBase64: Buffer.from(JSON.stringify({ schemaVersion: 1, repository: "FlappiBakuse/NexusPipeline", barriers: [] })).toString("base64"), ContentType: "application/json" }] }));
  const worker = path.join(runtime.runtimeDir, `.nxp/runtime/workers/update-${randomUUID().replaceAll("-", "")}.exe`);
  fs.mkdirSync(path.dirname(worker), {recursive:true});
  fs.copyFileSync(runtime.runtimeExe, worker);
  let status;
  try {
    runtime.startUpdateWorker(worker, ["apply-update", "--app-root", runtime.runtimeDir, "--staged", staging, "--web"], { NEXUS_TEST_HTTP_PLAN: plan });
    assert.equal(await runtime.waitFor(async () => {
      try { status = await (await runtime.api("GET", "api/status")).json(); return status.ready === true && !fs.existsSync(journal); }
      catch { return false; }
    }, 20000, 100), true, runtime.runtimeDiagnostic());
    const directory = path.join(runtime.runtimeDir, ".nxp/state/updates");
    const results = fs.readdirSync(directory).filter(file => file.endsWith(".result.json"));
    assert.equal(results.length, 1);
    const result = readJson(path.join(directory, results[0]));
    fs.copyFileSync(path.join(directory, results[0]), path.join(root, "update-commit-result.json"));
    assert.equal(result.Succeeded, true, JSON.stringify(result));
    assert.equal(result.Version, version);
    assert.equal(status.version, version);
    assert.equal(fs.readFileSync(path.join(runtime.runtimeDir, "README.md"), "utf8"), "current update readme");
    assert.deepEqual(receivedPayload(runtime.runtimeDir), targetPayload);
    assert.equal(fs.existsSync(path.join(runtime.runtimeDir, "wwwroot")), false);
    assert.equal(hash(runtime.runtimeExe), targetImage);
    assert.deepEqual(fs.readdirSync(path.join(runtime.runtimeDir,'resources/desktop/locales')).sort(),['en-US.pak','zh-CN.pak']);
    assert.equal(hash(user), original);
    assert.equal(fs.existsSync(staging), false);
    assert.equal(fs.existsSync(worker), false);
    assert.equal(fs.existsSync(path.join(runtime.runtimeDir, ".nxp-backup")), false);
    const receipt = readJson(path.join(directory, results[0].replace(".result.json", ".startup.json")));
    fs.copyFileSync(path.join(directory, results[0].replace(".result.json", ".startup.json")), path.join(root, "update-commit-startup.json"));
    assert.equal(receipt.TransactionId, result.TransactionId);
    assert.equal(receipt.ImageHash, targetImage);
    assert.deepEqual(receipt.Payload, targetPayload);
    const observed = readProcessIdentity(receipt.Identity.Pid);
    assert.equal(path.resolve(observed.executablePath).toLowerCase(), path.resolve(runtime.runtimeExe).toLowerCase());
  } catch (error) {
    const evidence = path.join(root, 'update-commit-failure');
    fs.mkdirSync(evidence, {recursive:true});
    for (const relative of ['.nxp-update/task.json','.nxp-version','.nxp/state/updates','logs']) {
      const source = path.join(runtime.runtimeDir, relative);
      if (fs.existsSync(source)) fs.cpSync(source, path.join(evidence,relative), {recursive:true});
    }
    fs.writeFileSync(path.join(evidence,'diagnostic.txt'),runtime.runtimeDiagnostic());
    throw error;
  } finally { await runtime.stopRuntime(); }
});

test("failed policy proof aborts only its exited worker and cannot repeat the apply loop", async () => {
  await runtime.prepareRuntime();
  const settings = path.join(runtime.runtimeDir, "config/settings.json");
  fs.writeFileSync(settings, JSON.stringify({ ...JSON.parse(fs.readFileSync(settings)), UpdateCheckEnabled: false }));
  const beforeImage = hash(runtime.runtimeExe);
  const user = path.join(runtime.runtimeDir, "history", "old-format.json");
  fs.mkdirSync(path.dirname(user), { recursive: true });
  fs.writeFileSync(user, '{"old":"retained"}\r\n');
  const original = hash(user);
  const staging = path.join(runtime.runtimeDir, ".nxp-update/staging/0.17.1.g1");
  const targetPayload = stageApplication(staging, "rejected update readme");
  const journal = path.join(runtime.runtimeDir, ".nxp-update/task.json");
  writeJournal(journal, "0.17.1", staging, targetPayload);
  const plan = path.join(root, "failed-update-policy-plan.json");
  fs.writeFileSync(plan, JSON.stringify({ RunId: runtime.runId, Exchanges: [{ Id: "failed-fresh-policy", Method: "GET",
    Url: "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json", Status: 503,
    BodyBase64: Buffer.from("unavailable").toString("base64"), ContentType: "text/plain" }] }));
  const worker = path.join(runtime.runtimeDir, `.nxp/runtime/workers/update-${randomUUID().replaceAll("-", "")}.exe`);
  fs.mkdirSync(path.dirname(worker), {recursive:true});
  fs.copyFileSync(runtime.runtimeExe, worker);
  try {
    runtime.startUpdateWorker(worker, ["apply-update", "--app-root", runtime.runtimeDir, "--staged", staging, "--web"], { NEXUS_TEST_HTTP_PLAN: plan });
    assert.equal(await runtime.waitFor(async () => {
      try { return (await (await runtime.api("GET", "api/status")).json()).ready === true && !fs.existsSync(journal); }
      catch { return false; }
    }, 10000, 100), true, runtime.runtimeDiagnostic());
    assert.equal(hash(runtime.runtimeExe), beforeImage);
    assert.equal(hash(user), original);
    assert.equal(fs.existsSync(staging), false);
    assert.equal(fs.existsSync(worker), false);
    assert.equal(fs.existsSync(path.join(runtime.runtimeDir, ".nxp-backup")), false);
    const results = fs.readdirSync(path.join(runtime.runtimeDir, ".nxp/state/updates")).filter(file => file.endsWith(".result.json"))
      .map(file => readJson(path.join(runtime.runtimeDir, ".nxp/state/updates", file))).filter(result => result.Version === "0.17.1");
    assert.equal(results.length, 1);
    assert.equal(results[0].Succeeded, false);
    assert.equal(results[0].Code, "policy-unavailable");
    const exchanges = fs.readFileSync(plan + ".receipts.jsonl", "utf8").trim().split("\n").map(JSON.parse);
    assert.equal(exchanges.length, 1);
    assert.equal(exchanges[0].matched, true);
  } finally { preserveFailedTransaction('failed-policy',journal);await runtime.stopRuntime(); }
});

for(const phase of ['BackupReady','SwapReady'])test(`whole application rollback preserves frozen old assets after ${phase} corruption`,async()=>{
  await runtime.prepareRuntime();
  const settings=path.join(runtime.runtimeDir,'config/settings.json');
  fs.writeFileSync(settings,JSON.stringify({...readJson(settings),UpdateCheckEnabled:false}));
  const oldPayload=receivedPayload(runtime.runtimeDir);
  const assets=[...readJson(path.join(runtime.runtimeDir,'resources/payload-manifest.json')).files.map(file=>file.path),'resources/payload-manifest.json'];
  const originalAssets=Object.fromEntries(assets.map(asset=>[asset,hash(path.join(runtime.runtimeDir,asset))]));
  const retained=path.join(runtime.runtimeDir,'.nxp/state/desktop/client-preferences.json');
  fs.mkdirSync(path.dirname(retained),{recursive:true});fs.writeFileSync(retained,'{"user":"retained"}\r\n');
  const userHash=hash(retained),unknown=path.join(runtime.runtimeDir,'unknown-user.bin');
  fs.writeFileSync(unknown,Buffer.from([0,255,13,10]));const unknownHash=hash(unknown);
  const staging=path.join(runtime.runtimeDir,'.nxp-update/staging/0.17.1.g1');
  const target=stageApplication(staging,'candidate README before corruption');
  const journal=path.join(runtime.runtimeDir,'.nxp-update/task.json');writeJournal(journal,'0.17.1',staging,target);
  const plan=path.join(root,`rollback-${phase}-policy.json`),pause=path.join(root,`rollback-${phase}.pause`);
  fs.writeFileSync(plan,JSON.stringify({RunId:runtime.runId,Exchanges:[{Id:'rollback-policy',Method:'GET',
    Url:'https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json',Status:200,
    BodyBase64:Buffer.from(JSON.stringify({schemaVersion:1,repository:'FlappiBakuse/NexusPipeline',barriers:[]})).toString('base64'),ContentType:'application/json'}]}));
  const worker=path.join(runtime.runtimeDir,`.nxp/runtime/workers/update-${randomUUID().replaceAll('-','')}.exe`);
  fs.mkdirSync(path.dirname(worker),{recursive:true});fs.copyFileSync(runtime.runtimeExe,worker);
  try {
    runtime.startUpdateWorker(worker,['apply-update','--app-root',runtime.runtimeDir,'--staged',staging,'--web'],{
      NEXUS_TEST_HTTP_PLAN:plan,NEXUS_TEST_UPDATE_PAUSE_PHASE:phase,NEXUS_TEST_UPDATE_PAUSE_FILE:pause});
    assert.equal(await runtime.waitFor(()=>fs.existsSync(pause),15000,50),true,runtime.runtimeDiagnostic());
    const frozen=readJson(journal);assert.equal(frozen.Phase,phase);assert.deepEqual(frozen.PreviousPayload,oldPayload);
    assert.equal(frozen.SwappedAssetCount,phase==='SwapReady'?4:0);
    const damaged=phase==='SwapReady'?path.join(runtime.runtimeDir,'resources/desktop/resources/app.asar'):path.join(staging,'README.md');
    fs.appendFileSync(damaged,'corruption');fs.unlinkSync(pause);
    assert.equal(await runtime.waitFor(async()=>{
      try{return (await (await runtime.api('GET','api/status')).json()).ready===true&&!fs.existsSync(journal);}
      catch{return false;}
    },20000,100),true,runtime.runtimeDiagnostic());
    assert.deepEqual(receivedPayload(runtime.runtimeDir),oldPayload);
    for(const asset of assets)assert.equal(hash(path.join(runtime.runtimeDir,asset)),originalAssets[asset],asset);
    assert.equal(hash(retained),userHash);assert.equal(hash(unknown),unknownHash);
    assert.equal(fs.readdirSync(path.join(runtime.runtimeDir,'resources/desktop/locales')).length,55);
    assert.equal(fs.existsSync(path.join(runtime.runtimeDir,'.nxp-backup')),false);
    const directory=path.join(runtime.runtimeDir,'.nxp/state/updates');
    const results=fs.readdirSync(directory).filter(file=>file.endsWith('.result.json')).map(file=>readJson(path.join(directory,file)));
    assert.ok(results.some(result=>result.Succeeded===false&&result.Code==='apply_failed'));
    fs.writeFileSync(path.join(root,`rollback-${phase}-evidence.json`),JSON.stringify({schemaVersion:1,status:'PASS',phase,oldPayload,restoredAssets:originalAssets,userBytesPreserved:true,results},null,2));
  }finally{
    preserveFailedTransaction('rollback-'+phase,journal);
    if(fs.existsSync(journal))fs.copyFileSync(journal,path.join(root,`rollback-${phase}-retained-journal.json`));
    if(fs.existsSync(pause))fs.unlinkSync(pause);
    await runtime.stopRuntime();
  }
});

test('unknown nested candidate bytes preserve the immutable backup and recovery journal',async()=>{
  await runtime.prepareRuntime();
  const oldPayload=receivedPayload(runtime.runtimeDir);
  const staging=path.join(runtime.runtimeDir,'.nxp-update/staging/0.17.1.g1');
  const target=stageApplication(staging,'candidate with an unknown nested file');
  const journal=path.join(runtime.runtimeDir,'.nxp-update/task.json');writeJournal(journal,'0.17.1',staging,target);
  const plan=path.join(root,'unknown-candidate-policy.json'),pause=path.join(root,'unknown-candidate.pause');
  fs.writeFileSync(plan,JSON.stringify({RunId:runtime.runId,Exchanges:[{Id:'unknown-policy',Method:'GET',
    Url:'https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json',Status:200,
    BodyBase64:Buffer.from(JSON.stringify({schemaVersion:1,repository:'FlappiBakuse/NexusPipeline',barriers:[]})).toString('base64'),ContentType:'application/json'}]}));
  const worker=path.join(runtime.runtimeDir,`.nxp/runtime/workers/update-${randomUUID().replaceAll('-','')}.exe`);
  fs.mkdirSync(path.dirname(worker),{recursive:true});fs.copyFileSync(runtime.runtimeExe,worker);
  try {
    const process=runtime.startUpdateWorker(worker,['apply-update','--app-root',runtime.runtimeDir,'--staged',staging,'--web'],{
      NEXUS_TEST_HTTP_PLAN:plan,NEXUS_TEST_UPDATE_PAUSE_PHASE:'SwapReady',NEXUS_TEST_UPDATE_PAUSE_FILE:pause});
    assert.equal(await runtime.waitFor(()=>fs.existsSync(pause),15000,50),true,runtime.runtimeDiagnostic());
    const unknown=path.join(runtime.runtimeDir,'resources/desktop/resources/user-sentinel.bin');
    fs.writeFileSync(unknown,Buffer.from([0,255,13,10,42]));const sentinel=hash(unknown);
    fs.unlinkSync(pause);
    assert.equal(await runtime.waitFor(()=>!readProcessIdentity(process.pid),10000,50),true,runtime.runtimeDiagnostic());
    const pending=readJson(journal);assert.equal(pending.Phase,'RollbackPending');
    assert.deepEqual(pending.PreviousPayload,oldPayload);assert.equal(hash(unknown),sentinel);
    assert.deepEqual(receivedPayload(path.join(runtime.runtimeDir,'.nxp-backup/previous')),oldPayload);
    for(const entry of pending.BackupInventory.Entries.filter(entry=>!entry.IsDirectory))
      assert.equal(hash(path.join(runtime.runtimeDir,'.nxp-backup/previous',entry.Path)),entry.Sha256);
    fs.writeFileSync(path.join(root,'unknown-candidate-evidence.json'),JSON.stringify({schemaVersion:1,status:'PASS',oldPayload,sentinelSha256:sentinel,backupPreserved:true,journal:pending},null,2));
  }finally{
    if(fs.existsSync(pause))fs.unlinkSync(pause);
    await runtime.stopRuntime();
  }
});
