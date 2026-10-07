"""Exact application inventory for the g0170 Host and desktop transaction."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import re
try:
    from .build_identity import validate_record
    from .runtime_profile import frozen as frozen_profile
except ImportError:
    from build_identity import validate_record
    from runtime_profile import frozen as frozen_profile

MAX_FILES=4096
MAX_BYTES=512*1024*1024
MAX_FILE_BYTES=256*1024*1024
MANIFEST_PATH='resources/payload-manifest.json'
ENTRY='resources/desktop/NexusPipeline.Desktop.exe'
ASAR='resources/desktop/resources/app.asar'
NATIVE='resources/desktop/resources/app.asar.unpacked/node_modules/@koromix/koffi-win32-x64/win32_x64/koffi.node'
FIELDS={'schemaVersion','product','productVersion','installationGeneration','rid','sourceSha','sourceTreeSha','buildId','frontendHash','desktop','files'}
DESKTOP_FIELDS={'entryPoint','appArchive','electronVersion','electronArchiveSha256','packageLockSha256','bootstrapProtocolVersion'}
PROFILE_FIELDS={'runtimeProfileId','runtimeProfileSha256','runtimeInventorySha256'}
RESERVED={'con','prn','aux','nul',*(f'com{i}' for i in '123456789¹²³'),*(f'lpt{i}' for i in '123456789¹²³')}

def safe_path(value: object) -> str:
    if type(value) is not str or len(value)>240 or value.startswith('/') or re.search(r'[\\:%?*"<>|\x00-\x1f]',value):raise ValueError('Unsafe application path')
    for part in value.split('/'):
        if part in ('','.','..') or part.endswith(('.',' ')) or part.split('.')[0].casefold() in RESERVED:raise ValueError('Unsafe application path')
    return value

def sha(file: Path) -> str:
    with file.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def runtime_files(root: Path) -> dict:
    value=json.loads((root/'desktop'/'runtime-files.json').read_text(encoding='utf-8'))
    return { 'resources/desktop/'+item['path']:item for item in value['files'] }

def kind(name: str,upstream: dict) -> str:
    if name=='NexusPipeline.exe':return 'host'
    if name=='README.md':return 'readme'
    if name==ENTRY:return 'desktop-entry'
    if name==ASAR:return 'desktop-app'
    if name in ('resources/desktop/LICENSE','resources/desktop/LICENSES.chromium.html','resources/desktop/koffi.LICENSE.txt'):return 'license'
    if name=='resources/desktop/version':return 'runtime-manifest'
    if name in upstream or name==NATIVE:return 'desktop-runtime'
    raise ValueError('Unexpected application file: '+name)

def inventory(directory: Path,upstream: dict) -> list[dict]:
    files=[];names=set();total=0
    for file in sorted(directory.rglob('*')):
        if file.is_symlink() or getattr(file.lstat(),'st_file_attributes',0) & 0x400:raise ValueError('Linked application file')
        if not file.is_file():continue
        name=file.relative_to(directory).as_posix()
        if name.startswith('plugins/') or name==MANIFEST_PATH:continue
        name=safe_path(name)
        size=file.stat().st_size;total+=size
        if size>MAX_FILE_BYTES or total>MAX_BYTES or name.casefold() in names:raise ValueError('Application limits or path collision')
        names.add(name.casefold())
        files.append({'path':name,'sizeBytes':size,'sha256':sha(file),'kind':kind(name,upstream)})
    if not 0<len(files)<=MAX_FILES:raise ValueError('Application file count')
    paths={item['path'] for item in files}
    if not ({'NexusPipeline.exe','README.md',ENTRY,ASAR,NATIVE}|set(upstream)).issubset(paths):raise ValueError('Incomplete application tree')
    for item in files:
        if item['path'] in upstream:
            frozen=upstream[item['path']]
            if item['sizeBytes']!=frozen['sizeBytes'] or item['sha256']!=frozen['sha256']:raise ValueError('Electron runtime byte mismatch')
        if item['kind'] in ('host','readme','desktop-entry','desktop-app') and item['sizeBytes']==0:raise ValueError('Empty required application file')
    return files

def create(directory: Path,record: dict,root: Path) -> dict:
    validate_record(record)
    inputs=record['buildInputs']
    value={key:record[key] for key in ('schemaVersion','product','productVersion','installationGeneration','rid','buildId','frontendHash')}
    value.update(sourceSha=inputs['sourceSha'],sourceTreeSha=inputs['sourceTreeSha'],desktop={
        'entryPoint':ENTRY,'appArchive':ASAR,'electronVersion':inputs['electronVersion'],'electronArchiveSha256':inputs['electronArchiveSha256'],
        'packageLockSha256':inputs['desktopPackageLockSha256'],'bootstrapProtocolVersion':1},files=inventory(directory,runtime_files(root)))
    if inputs['schemaVersion']==2:
        profile=frozen_profile(root)
        if any(inputs[key]!=profile[key] for key in PROFILE_FIELDS):raise ValueError('Frozen application runtime profile mismatch')
        value['desktop'].update(profile)
    target=directory/MANIFEST_PATH;target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes(json.dumps(value,sort_keys=True,separators=(',',':')).encode('utf-8'))
    return value

def validate(directory: Path,value: dict,record: dict,root: Path) -> dict:
    validate_record(record)
    inputs=record['buildInputs']
    if type(value) is not dict or set(value)!=FIELDS or type(value['desktop']) is not dict or set(value['desktop'])!=DESKTOP_FIELDS|(PROFILE_FIELDS if inputs['schemaVersion']==2 else set()):raise ValueError('Invalid application manifest fields')
    for key in ('schemaVersion','product','productVersion','installationGeneration','rid','buildId','frontendHash'):
        if value[key]!=record[key]:raise ValueError('Application identity mismatch: '+key)
    inputs=record['buildInputs']
    if value['sourceSha']!=inputs['sourceSha'] or value['sourceTreeSha']!=inputs['sourceTreeSha']:raise ValueError('Application source mismatch')
    expected={'entryPoint':ENTRY,'appArchive':ASAR,'electronVersion':inputs['electronVersion'],'electronArchiveSha256':inputs['electronArchiveSha256'],'packageLockSha256':inputs['desktopPackageLockSha256'],'bootstrapProtocolVersion':1}
    if inputs['schemaVersion']==2:
        profile=frozen_profile(root)
        if any(inputs[key]!=profile[key] for key in PROFILE_FIELDS):raise ValueError('Frozen application runtime profile mismatch')
        expected.update(profile)
    if value['desktop']!=expected or type(value['files']) is not list or value['files']!=inventory(directory,runtime_files(root)):raise ValueError('Application manifest byte or file set mismatch')
    return value


def main():
    import argparse
    from tools.build_identity import read_identity
    parser=argparse.ArgumentParser(allow_abbrev=False)
    for name in ('directory','identity','root'): parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args()
    create(args.directory,read_identity(args.identity),args.root)
    print('Application inventory created')
if __name__=='__main__':
    import sys
    sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
    main()
