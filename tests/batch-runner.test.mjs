import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import {execFileSync} from "node:child_process";
import {createScopePlan} from "./scope-plan.mjs";
import {loadBatch,saveBatch} from "./batch-runner.mjs";
const root=path.resolve(import.meta.dirname,"..");
const host=JSON.parse(fs.readFileSync(path.join(root,"tests/gates.json"))).repository==="Host";
test("report transport excludes Test Host assets and preserves owned evidence",()=>{
  const temporary=fs.mkdtempSync(path.join(process.env.NEXUS_TEST_ARTIFACT_ROOT||os.tmpdir(),"batch-transport-"));
  try {
    fs.mkdirSync(path.join(temporary,"test-host/wwwroot/.vite"),{recursive:true});
    fs.writeFileSync(path.join(temporary,"test-host/wwwroot/.vite/manifest.json"),"{}");
    fs.mkdirSync(path.join(temporary,"evidence/unit"),{recursive:true});
    fs.writeFileSync(path.join(temporary,"evidence/unit/unit-receipt.json"),"{}");
    fs.writeFileSync(path.join(temporary,"commands.log"),"owned command");
    const report=saveBatch({batch:{id:"control"},plan:{}},temporary,null,[],0,1,{cleanupComplete:true});
    assert.deepEqual(report.artifacts.map(item=>item.path).sort(),["commands.log","evidence/unit/unit-receipt.json"]);
    assert.ok(report.artifacts.every(item=>item.sizeBytes>0&&/^[a-f0-9]{64}$/.test(item.sha256)));
  } finally {fs.rmSync(temporary,{recursive:true});}
});
test("altered units, fingerprints, capacity and control manifests fail before work",()=>{
  const temporary=fs.mkdtempSync(path.join(process.env.NEXUS_TEST_ARTIFACT_ROOT||os.tmpdir(),"batch-input-"));
  const file=path.join(temporary,"plan.json");
  const partner=host?path.resolve(root,"../NexusPipeline-Plugins"):null;
  const base=execFileSync("git",["-C",root,"rev-parse","HEAD"],{encoding:"utf8"}).trim();
  const original=createScopePlan(root,{base,includeWorkingTree:true,...(partner?{partnerRoot:partner}:{})});
  const batch=original.control.units.length?"control":original.batches[0]?.id;
  const args=["--plan",file,"--batch",batch,...(partner?["--partner-root",partner]:[])];
  try {
    fs.writeFileSync(file,JSON.stringify(original));loadBatch(root,args);
    for(const alter of [plan=>plan.units[0].provides.push("invented"),plan=>plan.controlManifest.pop(),plan=>plan.sourceFingerprint="0".repeat(64),
      plan=>plan.capacityStatus="CAPACITY_EXCEEDED",plan=>plan.batches.push({id:"batch-06",units:[]}),plan=>plan.selected.pop()]) {
      const plan=structuredClone(original);alter(plan);fs.writeFileSync(file,JSON.stringify(plan));
      assert.throws(()=>loadBatch(root,args));
    }
  } finally {fs.rmSync(temporary,{recursive:true});}
});
