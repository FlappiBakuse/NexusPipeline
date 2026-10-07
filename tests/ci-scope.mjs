import {resolvePair, defaultPartner} from "./ci-inputs.mjs";
import fs from "node:fs";
import path from "node:path";
import {batchName, obligationNames} from "./ci-names.mjs";
import {createScopePlan} from "./scope-plan.mjs";
import {requiresPartner} from "./core-plan.mjs";
const root=path.resolve(import.meta.dirname,"..");
const base=process.env.PR_BASE_SHA,head=process.env.PR_HEAD_SHA;
if(!/^[a-f0-9]{40}$/.test(base??"")||!/^[a-f0-9]{40}$/.test(head??"")||!process.env.SCOPE_RESULT||!process.env.GITHUB_OUTPUT||!/^\d+$/.test(process.env.PR_NUMBER??"")) throw new Error("Complete PR identity required");
const identity={base,head,runId:process.env.GITHUB_RUN_ID,attempt:process.env.GITHUB_RUN_ATTEMPT,prNumber:Number(process.env.PR_NUMBER)};
const inputPair=resolvePair(root,"FlappiBakuse/NexusPipeline",identity);
const defaultInput = defaultPartner(root);
identity.inputPair=inputPair;
let plan=createScopePlan(root,{...identity,partnerSha:inputPair?.sources[1].testedSha});
if(plan.units.some(requiresPartner)) {
  const api=process.env.GITHUB_API_URL,token=process.env.GITHUB_TOKEN;
  if(!api||!token) throw new Error("Partner resolution requires read-only API");
  const read=async route=>{
    const response=await fetch(api+route,{headers:{Authorization:`Bearer ${token}`,Accept:"application/vnd.github+json"},signal:AbortSignal.timeout(8000)});
    if(!response.ok) throw new Error(`Fixed partner read failed: ${response.status}`);
    return response.json();
  };
  const ref=inputPair ? {object:{sha:inputPair.sources[1].testedSha}}
    : await read(`/repos/${defaultInput.repository}/git/ref/heads/${defaultInput.ref}`);
  const sha=ref.object?.sha;if(!/^[a-f0-9]{40}$/.test(sha??"")) throw new Error("Invalid fixed Plugins commit");
  const blob=await read(`/repos/FlappiBakuse/NexusPipeline-Plugins/contents/tests/policy.json?ref=${sha}`);
  if(blob.encoding!=="base64"||blob.type!=="file") throw new Error("Invalid fixed partner policy");
  const partnerPolicy=JSON.parse(Buffer.from(blob.content,"base64"));
  plan=createScopePlan(root,{...identity,partnerSha:sha,partnerPolicy});
}
if(plan.dirty||plan.capacityStatus!=="PLANNED") throw new Error("Dirty/CAPACITY_EXCEEDED scope");
const registry=JSON.parse(fs.readFileSync(path.join(root,"tests/gates.json")));
const matrix={include:plan.batches.map(batch=>({id:batch.id,name:batchName(batch,registry),partnerRequired:batch.units.some(requiresPartner),dotnetRequired:batch.units.some(unit=>unit.preparations.some(name=>name.includes("test-build")||name.includes("component:")||name.includes("production-package:"))||unit.id==="host.architecture.backend")}))};
if(matrix.include.length>5) throw new Error("Sixth batch rejected");
fs.mkdirSync(path.dirname(path.resolve(process.env.SCOPE_RESULT)),{recursive:true});
fs.writeFileSync(process.env.SCOPE_RESULT,JSON.stringify(plan,null,2)+"\n");
fs.appendFileSync(process.env.GITHUB_OUTPUT,`matrix=${JSON.stringify(matrix)}\ncount=${matrix.include.length}\ncontrol=${plan.control.units.length?"true":"false"}\npartnerSha=${plan.partnerSha??""}\n`);

if(process.env.GITHUB_STEP_SUMMARY) {
  const groups=[plan.control,...plan.batches].filter(batch=>batch.units.length);
  const summary=groups.map(batch=>`### ${batch.id==="control"?"Host / 控制检查":batchName(batch,registry)}\n\n${obligationNames(batch,registry).map(name=>`- ${name}`).join("\n")}`).join("\n\n");
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,summary+"\n");
}
