"""Build isolated same-generation applications from the runner's frozen source."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile


def main():
    parser=argparse.ArgumentParser(allow_abbrev=False)
    for name in ('source','repository','output','electron-archive'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--version',choices=['0.17.0','0.17.1'],required=True)
    parser.add_argument('--partner-sha',required=True)
    parser.add_argument('--full-locales',action='store_true')
    parser.add_argument('--full-dependencies',type=Path)
    args=parser.parse_args()
    if args.output.exists():parser.error('Fixture output already exists')
    source=args.output/'source'
    subprocess.run(['git','clone','--local','--no-hardlinks',str(args.repository),str(source)],check=True)
    shutil.copytree(args.source,source,dirs_exist_ok=True,ignore=shutil.ignore_patterns('.git','bin','obj','node_modules','out','.nxp-cache-identity','.nxp-test-lease.json','.generated'))
    project=source/'src/NexusPipeline.csproj'
    text=project.read_text(encoding='utf-8')
    if text.count('<Version>0.17.0</Version>')!=1:raise ValueError('Fixture source version mismatch')
    project.write_text(text.replace('<Version>0.17.0</Version>',f'<Version>{args.version}</Version>'),encoding='utf-8',newline='\n')
    for name in ('package.json','package-lock.json'):
        file=source/'desktop'/name;value=json.loads(file.read_bytes())
        value['version']=args.version
        if name=='package-lock.json':value['packages']['']['version']=args.version
        file.write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8',newline='\n')
    if args.full_locales:
        profile_file=source/'desktop/runtime-profile.json';profile=json.loads(profile_file.read_bytes())
        inventory_file=source/'desktop/runtime-files.json';inventory=json.loads(inventory_file.read_bytes())
        with zipfile.ZipFile(args.electron_archive) as archive:
            locales=[entry for entry in archive.infolist() if entry.filename.startswith('locales/') and entry.filename.endswith('.pak')]
            profile['id']='win-x64-full-locale-diagnostic';profile['locales']=sorted(Path(entry.filename).stem for entry in locales)
            profile_file.write_text(json.dumps(profile,indent=2)+'\n',encoding='utf-8',newline='\n')
            inventory['runtimeProfileId']=profile['id'];inventory['runtimeProfileSha256']=hashlib.sha256(profile_file.read_bytes()).hexdigest()
            inventory['files']=[entry for entry in inventory['files'] if not entry['path'].startswith('locales/')]
            for entry in locales:
                with archive.open(entry) as stream:digest=hashlib.file_digest(stream,'sha256').hexdigest()
                inventory['files'].append({'path':entry.filename,'sizeBytes':entry.file_size,'sha256':digest})
            inventory['files'].sort(key=lambda entry:entry['path'])
            inventory_file.write_text(json.dumps(inventory,indent=2)+'\n',encoding='utf-8',newline='\n')
    if args.full_dependencies:
        profile_file=source/'desktop/runtime-profile.json';profile=json.loads(profile_file.read_bytes())
        dependencies=[]
        for package in ('koffi','@koromix/koffi-win32-x64'):
            directory=args.full_dependencies/package
            for file in directory.rglob('*'):
                if file.is_symlink() or file.is_junction():raise ValueError('Linked reference dependency')
                if file.is_file():dependencies.append(file.relative_to(args.full_dependencies).as_posix())
        profile['dependencyFiles']=sorted(dependencies)
        profile_file.write_text(json.dumps(profile,indent=2)+'\n',encoding='utf-8',newline='\n')
        inventory_file=source/'desktop/runtime-files.json';inventory=json.loads(inventory_file.read_bytes())
        inventory['runtimeProfileSha256']=hashlib.sha256(profile_file.read_bytes()).hexdigest()
        inventory_file.write_text(json.dumps(inventory,indent=2)+'\n',encoding='utf-8',newline='\n')
    sys.path.insert(0,str(source))
    from tools.application_build import prepare
    from tools.application_payload import create
    from tools.build_identity import read_identity
    commit=subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()
    script='import {workingTreeSha} from '+json.dumps((source/'tests/control-inputs.mjs').as_uri())+';process.stdout.write(workingTreeSha(process.argv[1]));'
    tree=subprocess.check_output(['node','--input-type=module','-e',script,str(source)],text=True).strip()
    inputs=prepare(source,args.output/'inputs',source_sha=commit,source_tree_sha=tree,partner_sha=args.partner_sha,workflow_sha=commit,electron_archive=args.electron_archive)
    application=args.output/'application'
    subprocess.run(['dotnet','publish',str(project),'-c','Release','-r','win-x64','--self-contained','false',
        '-p:PublishSingleFile=true','-p:DebugType=none','-p:DebugSymbols=false','-p:NexusTestHost=true','-p:UseSharedCompilation=false','--disable-build-servers',
        '-p:NexusFrontendProps='+inputs['frontendProps'],'-p:NexusBuildIdentityPath='+inputs['identity'],'-o',str(application),'--nologo'],cwd=source,check=True)
    shutil.copytree(inputs['desktopBundle'],application/'resources/desktop')
    shutil.copy2(source/'README.md',application/'README.md')
    record=read_identity(Path(inputs['identity']));manifest=create(application,record,source)
    complete={'version':args.version,'buildId':record['buildId'],'sourceTreeSha':tree,'manifestSha256':hashlib.sha256((application/'resources/payload-manifest.json').read_bytes()).hexdigest(),
        'imageSha256':hashlib.sha256((application/'NexusPipeline.exe').read_bytes()).hexdigest(),'fullLocales':args.full_locales,'classification':'LOCAL_DIAGNOSTIC'}
    (args.output/'application.complete.json').write_text(json.dumps(complete,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(complete),flush=True)


if __name__=='__main__':main()
