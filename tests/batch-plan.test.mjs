import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import {coreUnits, allocateUnits} from "./core-plan.mjs";
import {planForChanges, readRegistry} from "./scope-plan.mjs";
const root=path.resolve(import.meta.dirname,"..");
const {registry}=readRegistry(root);
const policy=JSON.parse(fs.readFileSync(path.join(root,"tests/policy.json")));
test("empty or unknown inputs preserve the entire applicable core",()=>{
  for(const changes of [[],[{status:"M",path:"unknown.extension"}]]) {
    const plan=planForChanges(root,changes,registry,policy);
    const units=coreUnits(plan.selected,registry,policy);
    assert.ok(units.length>5);
    if(registry.repository==="Plugins") for(const name of Object.keys(policy.plugins)) {
      assert.ok(units.some(unit=>unit.id===`plugins.runtime:${name}`),name);
      assert.ok(plan.selected.some(item=>item.id===`plugins.plugin.package:${name}`),name);
    }
    assert.deepEqual(allocateUnits(units,{source:"same"}),allocateUnits([...units].reverse(),{source:"same"}));
  }
});
test("docs only uses a lightweight control without core batches",()=>{
  const selected=planForChanges(root,[{status:"M",path:"docs/testing/README.md"}],registry,policy).selected;
  const plan=allocateUnits(coreUnits(selected,registry,policy),{});
  assert.equal(plan.batches.length,0);
  assert.equal(plan.control.units.length,1);
  assert.equal(plan.estimatedTotalJobs,5);
});
test("0/1/13/50/100 synthetic plugins never cause a sixth batch or lose obligations",()=>{
  for(const count of [0,1,13,50,100]) {
    const units=Array.from({length:count},(_,i)=>({id:`plugin-${i}`,kind:"plugin",artifact:`plugin-${i}`,pluginKind:"managed-code",
      provides:[`proof-${i}`],preparations:["shared"],expectedCaseIds:[`case-${i}`]}));
    const plan=allocateUnits(units,{source:"fixture"});
    assert.ok(plan.batches.length<=5);assert.ok(plan.estimatedTotalJobs<=10);
    assert.equal(plan.units.length,count);
    assert.equal(plan.requiredObligations.length,count);
    assert.equal(plan.batches.reduce((sum,b)=>sum+b.units.length,0)+plan.unplacedUnits.length,count);
    if(count>=50) assert.equal(plan.capacityStatus,"CAPACITY_EXCEEDED");
  }
});
test("oversized unit fails capacity without becoming N/A",()=>{
  const unit={id:"slow",provides:["essential"],preparations:[],kind:"plugin"};
  const plan=allocateUnits([unit],{}, {unitMs:{slow:130001}});
  assert.equal(plan.capacityStatus,"CAPACITY_EXCEEDED");
  assert.deepEqual(plan.requiredObligations,["essential"]);
  assert.equal(plan.unplacedUnits[0].id,"slow");
});
test("production and test input identities cannot share prepare keys",()=>{
  const unit={id:"a",provides:["a"],preparations:["sdk"],kind:"gate"};
  const a=allocateUnits([unit],{mode:"production",sdk:"a"});
  const b=allocateUnits([unit],{mode:"test",sdk:"a"});
  assert.notEqual(a.units[0].prepareKey,b.units[0].prepareKey);
});
