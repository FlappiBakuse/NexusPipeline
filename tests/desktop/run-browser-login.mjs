import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {pathToFileURL} from 'node:url';
import {createRunMarker,stopSpawnedService} from '../support/test-runtime.mjs';
import {randomUUID} from 'node:crypto';
const [inputs,directory,application]=process.argv.slice(2).map(value=>path.resolve(value));
fs.mkdirSync(directory,{recursive:true});
const fixture=path.join(directory,'fixture'),report=path.join(directory,'browser-login-report.json');
if(fs.existsSync(fixture)||fs.existsSync(report))throw new Error('Existing browser diagnostic');
fs.cpSync(path.join(inputs,'desktop/application'),fixture,{recursive:true});
fs.copyFileSync(new URL('./browser-login-driver.cjs',import.meta.url),path.join(fixture,'browser-login-driver.cjs'));
const metadata=JSON.parse(fs.readFileSync(path.join(fixture,'package.json')));metadata.main='browser-login-driver.cjs';fs.writeFileSync(path.join(fixture,'package.json'),JSON.stringify(metadata));
const {createPackageWithOptions}=await import(pathToFileURL(path.join(inputs,'desktop/source/node_modules/@electron/asar/lib/asar.js')));
const nativeRoot=path.join(directory,'runtime');fs.cpSync(path.join(application,'resources/desktop'),nativeRoot,{recursive:true});
const archive=path.join(nativeRoot,'resources/app.asar');await createPackageWithOptions(fixture,archive,{unpack:'**/*.node'});
const executable=path.join(nativeRoot,'NexusPipeline.Desktop.exe');
const nonce=randomUUID(),marker=path.join(directory,'run-marker.json'),exitFile=path.join(directory,'client.exit');
const child=spawn(executable,[],{env:{...process.env,NEXUS_BROWSER_LOGIN_REPORT:report},windowsHide:true,stdio:['ignore','pipe','pipe']});
let diagnostic='';child.stdout.on('data',bytes=>{diagnostic+=bytes;});child.stderr.on('data',bytes=>{diagnostic+=bytes;});
await new Promise((resolve,reject)=>{child.once('spawn',resolve);child.once('error',reject);});
createRunMarker(marker,executable,child,{nonce,runId:path.basename(directory)});
let timeout=false,cleanup;
const timer=setTimeout(()=>{timeout=true;cleanup=stopSpawnedService({child,exitFile,markerPath:marker});cleanup.catch(()=>{});},45000);
try {
  const exit=await new Promise((resolve,reject)=>{child.once('error',reject);child.once('exit',resolve);});
  fs.writeFileSync(path.join(directory,'process-result.json'),JSON.stringify({exit,timeout,diagnostic},null,2));
  if(timeout||exit!==0||!fs.existsSync(report)||JSON.parse(fs.readFileSync(report)).status!=='PASS')throw new Error('Controlled native browser login failed; inspect '+report);
} finally {
  clearTimeout(timer);
  await (cleanup??stopSpawnedService({child,exitFile,markerPath:marker}));
}
console.log('Controlled native browser isolation, capture and cleanup PASS');
