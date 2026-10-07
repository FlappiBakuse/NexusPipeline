import fs from 'node:fs';
import path from 'node:path';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {createRunMarker,stopSpawnedService,findAvailablePort,registerHandoffProcess} from '../support/test-runtime.mjs';
import {readProcessIdentity,isProcessAlive} from '../support/windows-process.mjs';
const base=path.resolve(process.argv[2]),root=path.join(base,'software'),exe=path.join(root,'NexusPipeline.exe');
const report=path.join(base,'client-report.json'),exitFile=path.join(base,'host.exit'),marker=path.join(base,'run-marker.json');
if(fs.existsSync(report)||fs.existsSync(marker))throw new Error('Existing test evidence');
const port=await findAvailablePort(),nonce=randomUUID(),runId=path.basename(base);
fs.mkdirSync(path.join(root,'config'),{recursive:true});
fs.writeFileSync(path.join(root,'config/settings.json'),JSON.stringify({WebPort:port,LightweightMode:false,OpenDesktopOnStartup:false,UpdateCheckEnabled:false}));
fs.mkdirSync(path.join(root,'.nxp/state/desktop'),{recursive:true});
fs.writeFileSync(path.join(root,'.nxp/state/desktop/client-preferences.json'),JSON.stringify({schemaVersion:1,preferences:{locale:'en-US',theme:'light'}}));
const env={...process.env,NEXUS_TEST_MODE:'test-host',NEXUS_TEST_HOST:'1',NEXUS_TEST_RUN_ID:runId,
  NEXUS_TEST_HOST_DIR:root,NEXUS_TEST_HOST_EXIT_FILE:exitFile,NEXUS_TEST_OWNERSHIP_NONCE:nonce,
  NEXUS_SYSTEM_RUNTIME_NAME:runId,NEXUS_SYSTEM_ACTION_DRYRUN:'1',NEXUS_DESKTOP_TEST_REPORT:report,
  HTTP_PROXY:'',HTTPS_PROXY:'',http_proxy:'',https_proxy:'',NO_PROXY:'127.0.0.1,localhost'};
const child=spawn(exe,[],{cwd:root,env,stdio:['pipe','pipe','pipe'],windowsHide:true});
let output='',desktopFamily=[];child.stdout.on('data',bytes=>{output+=bytes;});child.stderr.on('data',bytes=>{output+=bytes;});
createRunMarker(marker,exe,child,{nonce,identityFile:exitFile+'.identity.json',handoffExecutablePath:exe,runId});
try {
  const deadline=Date.now()+60000;
  while(Date.now()<deadline&&!fs.existsSync(report)){
    if(fs.existsSync(exitFile+'.identity.json')) {
      const receipt=JSON.parse(fs.readFileSync(exitFile+'.identity.json','utf8').replace(/^\uFEFF/u,''));
      if(receipt.pid!==child.pid)registerHandoffProcess(marker,receipt.pid,{expectedInstanceId:receipt.instanceId,expectedHandoffId:receipt.restartHandoffId});
    }
    await new Promise(resolve=>setTimeout(resolve,100));
  }
  if(!fs.existsSync(report))throw new Error('Desktop observation timeout: '+fs.readFileSync(path.join(base,'client-progress.json'),'utf8'));
  const result=JSON.parse(fs.readFileSync(report));if(result.status!=='PASS')throw new Error(result.error);
  const identity=await (await fetch(`http://127.0.0.1:${port}/api/status?view=identity`)).json();
  if(!identity.ready||identity.installationGeneration!=='g0170'||!identity.desktopBuildId||fs.existsSync(path.join(root,'wwwroot')))throw new Error('Host application identity rejected');
  let session;
  const stateDeadline=Date.now()+3000;
  do {
    session=JSON.parse(fs.readFileSync(path.join(root,'.nxp/runtime/desktop/session.json'))).record;
    if(session.windowState==='hidden')break;
    await new Promise(resolve=>setTimeout(resolve,50));
  }while(Date.now()<stateDeadline);
  if(session.main.pid!==result.pid||session.windowState!=='hidden')throw new Error('Authenticated session state disagrees');
  desktopFamily=session.family;
  fs.writeFileSync(path.join(base,'host-client-evidence.json'),JSON.stringify({schemaVersion:1,status:'PASS',classification:'LOCAL_DIAGNOSTIC',modeException:'Normal desktop lifecycle requires LightweightMode=false',hostIdentity:identity,client:result,sessionId:session.sessionId,real:['Electron window','eight business routes','embedded Vue','authenticated IPC','client preferences','validated clipboard IPC','hide on close','Host restart retains unsaved input','explicit discard navigation','renderer crash recovery'],substituted:['instrumented test ASAR','native dialog acknowledgement','system clipboard write']},null,2));
  console.log('Authenticated real desktop and eight business routes PASS');
}finally {
  fs.writeFileSync(path.join(base,'host-output.txt'),output);
  fs.writeFileSync(exitFile,'stop\n');
  const shutdownDeadline=Date.now()+6000;
  while(Date.now()<shutdownDeadline) {
    const record=JSON.parse(fs.readFileSync(marker,'utf8'));
    const owned=[record.pid,...(record.handoffProcesses??[]).map(value=>value.pid)];
    if(owned.every(pid=>!readProcessIdentity(pid)))break;
    await new Promise(resolve=>setTimeout(resolve,100));
  }
  await stopSpawnedService({child,exitFile,pidFilePath:path.join(root,'.nxp/runtime/service.pid'),markerPath:marker,exitWaitPollMs:100});
  for(const identity of desktopFamily)if(isProcessAlive(identity.pid))throw new Error('Desktop family remains alive after Host exit: '+identity.pid);
  if(fs.existsSync(path.join(root,'.nxp/runtime/desktop/session.json')))throw new Error('Owned desktop session remains after Host exit');
  fs.writeFileSync(path.join(base,'cleanup-evidence.json'),JSON.stringify({status:'PASS',hostExited:true,desktopFamilyExited:desktopFamily,sessionRemoved:true},null,2));
}
