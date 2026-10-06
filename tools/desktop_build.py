"""Build the frozen Electron client; user state is never part of this tree."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import zipfile

try:
    from .build_identity import read_identity, validate_record, canonical_json
    from .pe_icon import replace_icon
except ImportError:
    from build_identity import read_identity, validate_record, canonical_json
    from pe_icon import replace_icon


def digest(file: Path) -> str:
    with file.open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()


def build(root: Path, output: Path, identity: Path, electron_archive: Path, *, install=True) -> dict:
    root,output,identity,electron_archive=(value.resolve() for value in (root,output,identity,electron_archive))
    if output.exists(): raise ValueError('Desktop output already exists')
    record=read_identity(identity);validate_record(record)
    if canonical_json(record)!=identity.read_bytes(): raise ValueError('Build identity is not canonical')
    inputs=record['buildInputs'];runtime=json.loads((root/'desktop'/'electron-runtime.json').read_text(encoding='utf-8'))
    for file,key in ((root/'frontend'/'package-lock.json','frontendPackageLockSha256'),(root/'desktop'/'package-lock.json','desktopPackageLockSha256'),(electron_archive,'electronArchiveSha256')):
        if digest(file)!=inputs[key]: raise ValueError('Frozen desktop input mismatch: '+key)
    if runtime['version']!=inputs['electronVersion'] or runtime['archiveSha256']!=inputs['electronArchiveSha256']: raise ValueError('Electron release lock mismatch')
    output.mkdir(parents=True)
    source=output/'source';shutil.copytree(root/'desktop',source,ignore=shutil.ignore_patterns('node_modules','out'))
    npm='npm.cmd' if os.name=='nt' else 'npm'
    environment={**os.environ,'ELECTRON_SKIP_BINARY_DOWNLOAD':'1'}
    if install: subprocess.run([npm,'ci','--no-audit','--no-fund'],cwd=source,env=environment,check=True)
    else: raise ValueError('Dependency installation is mandatory')
    subprocess.run([npm,'run','build'],cwd=source,env=environment,check=True)
    bundle=output/'bundle';bundle.mkdir()
    with zipfile.ZipFile(electron_archive) as archive:
        names=set()
        for item in archive.infolist():
            name=item.filename
            if name.startswith('/') or '\\' in name or ':' in name or any(part in ('','.','..') for part in name.rstrip('/').split('/')) or name.casefold() in names or item.external_attr>>16 & 0o170000 == 0o120000: raise ValueError('Unsafe Electron archive path')
            names.add(name.casefold())
            if item.is_dir(): continue
            if item.file_size>256*1024*1024: raise ValueError('Electron archive file limit')
            destination=bundle.joinpath(*name.split('/'));destination.parent.mkdir(parents=True,exist_ok=True)
            with archive.open(item) as stream,destination.open('xb') as target: shutil.copyfileobj(stream,target)
    (bundle/'electron.exe').rename(bundle/'NexusPipeline.Desktop.exe')
    icon=root/'src'/'NexusPipeline.ico'
    replace_icon(bundle/'NexusPipeline.Desktop.exe',icon)
    shutil.copy2(icon,bundle/'resources'/'NexusPipeline.ico')
    default=bundle/'resources'/'default_app.asar'
    if default.exists(): default.unlink()
    app=output/'application';app.mkdir()
    for folder in ('main','preload','shared'): shutil.copytree(source/'out'/folder,app/folder)
    shutil.copytree(source/'src'/'bootstrap',app/'bootstrap')
    shutil.copy2(identity,app/'desktop-build.json')
    package={'name':'nexuspipeline-desktop','version':record['productVersion'],'private':True,'main':'main/index.js'}
    (app/'package.json').write_text(json.dumps(package,separators=(',',':')),encoding='utf-8')
    for package_path in ('koffi','@koromix/koffi-win32-x64'):
        shutil.copytree(source/'node_modules'/package_path,app/'node_modules'/package_path)
    pack=output/'pack.mjs'
    pack.write_text('import {createPackageWithOptions,extractFile} from '+json.dumps((source/'node_modules'/'@electron'/'asar'/'lib'/'asar.js').as_uri())+';\n'
        +'import fs from "node:fs";\nawait createPackageWithOptions(process.argv[2],process.argv[3],{unpack:"**/*.node"});\n'
        +'if(!extractFile(process.argv[3],"desktop-build.json").equals(fs.readFileSync(process.argv[4])))throw new Error("ASAR build identity mismatch");\n',encoding='utf-8')
    archive=bundle/'resources'/'app.asar';archive.parent.mkdir(exist_ok=True)
    subprocess.run(['node',str(pack),str(app),str(archive),str(identity)],cwd=source,check=True)
    for package_path,label in (('koffi','koffi'),('@koromix/koffi-win32-x64','koffi-win32-x64')):
        license_file=source/'node_modules'/package_path/'LICENSE.txt'
        if not license_file.exists(): license_file=source/'node_modules'/package_path/'LICENSE'
        if license_file.exists(): shutil.copy2(license_file,bundle/(label+'.LICENSE.txt'))
    result={'schemaVersion':1,'buildId':record['buildId'],'electronVersion':runtime['version'],'electronArchiveSha256':digest(electron_archive),'bundle':str(bundle),'appArchiveSha256':digest(archive),'iconSha256':digest(icon)}
    (output/'build-result.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    return result


def main():
    parser=argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument('--root',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--identity',type=Path,required=True);parser.add_argument('--electron-archive',type=Path,required=True)
    args=parser.parse_args()
    try: print(json.dumps(build(args.root,args.output,args.identity,args.electron_archive)))
    except (ValueError,OSError,subprocess.CalledProcessError) as error: parser.exit(1,str(error)+'\n')
if __name__=='__main__':main()
