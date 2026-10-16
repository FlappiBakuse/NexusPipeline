"""Build the bounded maintenance programs from the frozen application inputs."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

def prepare(root: Path, output: Path, record: dict, *, test_host: bool = False) -> dict:
    output.mkdir(parents=True, exist_ok=False)
    inputs = record['buildInputs']
    result = {}
    for role, project, image in [
        ('updater', 'src/NexusPipeline.Updater/NexusPipeline.Updater.csproj', 'NexusPipeline.update.exe'),
        ('uninstaller', 'src/NexusPipeline.Uninstaller/NexusPipeline.Uninstaller.csproj', 'NexusPipeline.uninstall.exe'),
        ('installer-metadata', 'tools/NexusPipeline.InstallerMetadata/NexusPipeline.InstallerMetadata.csproj', 'nxp-metadata-helper.exe')]:
        identity = output/(role+'.json')
        identity.write_text(json.dumps({'schemaVersion':1, 'role':role, 'productVersion':record['productVersion'],
            'installationGeneration':record['installationGeneration'], 'sourceSha':inputs['sourceSha'],
            'sourceTreeSha':inputs['sourceTreeSha'], 'buildId':record['buildId']}, sort_keys=True, separators=(',',':')), encoding='utf-8')
        directory = output/role
        command = ['dotnet','publish',str(root/project),'-c','Release','-r','win-x64','--self-contained','false',
            '-p:Version='+record['productVersion'], '-p:PublishSingleFile=true','-p:DebugType=none','-p:DebugSymbols=false','-p:UseSharedCompilation=false',
            '--disable-build-servers', '-p:NexusTestHost='+str(test_host).lower(), '-p:NexusToolBuildIdentityPath='+str(identity),
            '-o',str(directory),'--nologo']
        subprocess.run(command,cwd=root,check=True)
        result[role] = str(directory/image)
        items = subprocess.check_output(['dotnet','msbuild',str(root/project),'-nologo',
            '-p:NexusTestHost='+str(test_host).lower(), '-p:NexusToolBuildIdentityPath='+str(identity),
            '-getItem:Compile,EmbeddedResource,ProjectReference', '-getProperty:AssemblyName,TargetFramework,ApplicationManifest'],cwd=root,text=True,encoding='utf-8')
        evidence = {'role':role,'image':str(directory/image),'sizeBytes':(directory/image).stat().st_size,
            'sha256':hashlib.sha256((directory/image).read_bytes()).hexdigest(),'identity':json.loads(identity.read_bytes()),
            'command':command,'items':json.loads(items)}
        (output/(role+'-build.json')).write_text(json.dumps(evidence,indent=2),encoding='utf-8')
    maintenance = root/'src/NexusPipeline.Maintenance/NexusPipeline.Maintenance.csproj'
    closure = subprocess.check_output(['dotnet','msbuild',str(maintenance),'-nologo','-p:NexusTestHost='+str(test_host).lower(),
        '-getItem:Compile,EmbeddedResource,ProjectReference,PackageReference','-getProperty:AssemblyName'],cwd=root,text=True,encoding='utf-8')
    (output/'maintenance-closure.json').write_text(closure,encoding='utf-8')
    return result
