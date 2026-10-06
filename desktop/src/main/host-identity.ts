import crypto from "node:crypto";
import fs from "node:fs";
export interface BuildRecord {schemaVersion: 1; product: "NexusPipeline"; productVersion: string; installationGeneration: "g0170"; rid: "win-x64"; buildId: string; frontendHash: string; bootstrapProtocolVersion: 1; buildInputs: Record<string,unknown>;}
export function canonical(value: unknown,depth=0): string {
  if(depth>4) throw new Error("Invalid build identity nesting");
  if(typeof value==="string") {if(!/^[ -~]*$/.test(value)) throw new Error("Invalid build identity text");return JSON.stringify(value);}
  if(typeof value==="number"&&Number.isSafeInteger(value)&&value>=0) return String(value);
  if(!value||Array.isArray(value)||typeof value!=="object") throw new Error("Invalid build identity value");
  return '{'+Object.keys(value).sort().map(key=>canonical(key)+':'+canonical((value as Record<string,unknown>)[key],depth+1)).join(',')+'}';
}
export function fields(value: unknown,names: string[]): asserts value is Record<string,unknown> {
  if(!value||typeof value!=="object"||Array.isArray(value)||Object.keys(value).sort().join('\n')!==names.toSorted().join('\n')) throw new Error("Invalid supervisor fields");
}
export function readBuild(file: string): BuildRecord {
  const bytes=fs.readFileSync(file);if(bytes.length>65536) throw new Error("Invalid build identity size");
  const value=JSON.parse(new TextDecoder("utf-8",{fatal:true}).decode(bytes));
  fields(value,['schemaVersion','product','productVersion','installationGeneration','rid','buildId','frontendHash','bootstrapProtocolVersion','buildInputs']);
  const inputs=value.buildInputs;
  fields(inputs,['schemaVersion','productVersion','generation','rid','sourceSha','sourceTreeSha','partnerSha','workflowSha','frontendHash','frontendPackageLockSha256','desktopPackageLockSha256','electronVersion','electronArchiveSha256','bootstrapProtocolVersion','toolchain']);
  fields(inputs.toolchain,['dotnetSdkVersion','dotnetRuntimeVersion','nodeVersion','npmVersion','pythonVersion','innoSetupVersion','innoCompilerSha256','innoDistributionSha256']);
  if(value.schemaVersion!==1||value.product!=="NexusPipeline"||value.rid!=="win-x64"||value.installationGeneration!=="g0170"||value.bootstrapProtocolVersion!==1
    ||inputs.schemaVersion!==1||inputs.bootstrapProtocolVersion!==1||inputs.generation!==value.installationGeneration||inputs.rid!==value.rid||inputs.productVersion!==value.productVersion||inputs.frontendHash!==value.frontendHash
    ||!/^0\.17\.0$/.test(String(value.productVersion))||!/^\d+\.\d+\.\d+$/.test(String(inputs.electronVersion))) throw new Error("Invalid build identity target");
  for(const key of ['sourceSha','sourceTreeSha','partnerSha','workflowSha']) if(!/^[0-9a-f]{40}$/.test(String(inputs[key]))) throw new Error("Invalid build source");
  for(const key of ['frontendHash','frontendPackageLockSha256','desktopPackageLockSha256','electronArchiveSha256']) if(!/^[0-9a-f]{64}$/.test(String(inputs[key]))) throw new Error("Invalid build hash");
  for(const [key,item] of Object.entries(inputs.toolchain)) if(typeof item!=="string"||!new RegExp(key.endsWith('Sha256')?'^[0-9a-f]{64}$':'^[ -~]+$').test(item)) throw new Error("Invalid toolchain identity");
  if(crypto.createHash('sha256').update(canonical(inputs)).digest('hex')!==value.buildId||canonical(value)!==bytes.toString('utf8')) throw new Error("Invalid canonical build identity");
  return Object.freeze(value) as unknown as BuildRecord;
}
export interface HostState {state: "starting"|"ready"|"restarting"|"stopping";actualPort: number;instanceId: string;previousInstanceId: string;handoffId: string;desktopBuildId: string;frontendBuildId: string;}
export async function verifyHttp(state: HostState,build: BuildRecord): Promise<boolean> {
  if(state.state!=="ready"||!Number.isInteger(state.actualPort)||state.actualPort<1||state.actualPort>65535||state.desktopBuildId!==build.buildId||state.frontendBuildId!==build.frontendHash) return false;
  try {
    const response=await fetch(`http://127.0.0.1:${state.actualPort}/api/status`,{signal:AbortSignal.timeout(3000),redirect:'error',cache:'no-store'});
    if(!response.ok) return false;
    const status=await response.json() as Record<string,unknown>;
    return status.service==='NexusPipeline.g0170'&&status.controlApiVersion===1&&status.actualPort===state.actualPort&&status.ready===true&&status.instanceId===state.instanceId&&status.restartHandoffId===state.handoffId&&status.desktopBuildId===build.buildId&&status.frontendBuildId===build.frontendHash&&status.installationGeneration==='g0170'&&status.bootstrapProtocolVersion===1;
  } catch {return false;}
}
