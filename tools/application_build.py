"""Freeze build inputs before embedding Vue and assembling the desktop client."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import urllib.request
try:
    from .build_identity import canonical_json,create_record
    from .embed_frontend import generate
    from .desktop_build import build as build_desktop
    from .runtime_profile import frozen as frozen_profile
except ImportError:
    from build_identity import canonical_json,create_record
    from embed_frontend import generate
    from desktop_build import build as build_desktop
    from runtime_profile import frozen as frozen_profile

def command(*args: str) -> str:return subprocess.check_output(args,text=True,encoding='utf-8').strip()
def sha(file: Path) -> str:
    with file.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def prepare_compilation(root: Path,output: Path,*,source_sha: str,source_tree_sha: str,partner_sha: str,workflow_sha: str) -> dict:
    try:
        from .host_release import project_version, normalized_tag
    except ImportError:
        from host_release import project_version, normalized_tag
    version = project_version(root)
    if normalized_tag(version)[1:] != version:
        raise ValueError('Host product version is not canonical')
    desktop_package = json.loads((root/'desktop'/'package.json').read_bytes())
    desktop_lock = json.loads((root/'desktop'/'package-lock.json').read_bytes())
    if desktop_package.get('version') != version or desktop_lock.get('version') != version or desktop_lock.get('packages', {}).get('', {}).get('version') != version:
        raise ValueError('Host and desktop product versions disagree')
    profile = frozen_profile(root)
    if output.exists():raise ValueError('Application inputs output already exists')
    output.mkdir(parents=True)
    runtime=json.loads((root/'desktop'/'electron-runtime.json').read_text(encoding='utf-8'))
    index=generate(root/'frontend'/'dist',output/'frontend')
    npm='npm.cmd' if os.name=='nt' else 'npm'
    toolchain={'dotnetSdkVersion':command('dotnet','--version'),'dotnetRuntimeVersion':'10.0.12','nodeVersion':command('node','--version').removeprefix('v'),
        'npmVersion':command(npm,'--version'),'pythonVersion':platform.python_version(),'innoSetupVersion':'6.7.3',
        'innoCompilerSha256':'0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7','innoDistributionSha256':'9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'}
    if 'Microsoft.NETCore.App 10.0.12 ' not in command('dotnet','--list-runtimes'):raise ValueError('Required .NET runtime is missing')
    inputs={'schemaVersion':2,'productVersion':version,'generation':'g0170','rid':'win-x64','sourceSha':source_sha,'sourceTreeSha':source_tree_sha,'partnerSha':partner_sha,'workflowSha':workflow_sha,
        'frontendHash':index['frontendHash'],'frontendPackageLockSha256':sha(root/'frontend'/'package-lock.json'),'desktopPackageLockSha256':sha(root/'desktop'/'package-lock.json'),
        'electronVersion':runtime['version'],'electronArchiveSha256':runtime['archiveSha256'],'bootstrapProtocolVersion':1,'toolchain':toolchain,**profile}
    record=create_record(inputs)
    identity=output/'desktop-build.json';identity.write_bytes(canonical_json(record))
    (output/'build-inputs.json').write_bytes(canonical_json(inputs))
    return {'identity':str(identity),'frontendProps':str(output/'frontend'/'embedded-frontend.props'),'buildId':record['buildId']}

def prepare(root: Path,output: Path,*,source_sha: str,source_tree_sha: str,partner_sha: str,workflow_sha: str,electron_archive: Path|None=None) -> dict:
    result=prepare_compilation(root,output,source_sha=source_sha,source_tree_sha=source_tree_sha,partner_sha=partner_sha,workflow_sha=workflow_sha)
    runtime=json.loads((root/'desktop'/'electron-runtime.json').read_text(encoding='utf-8'))
    if electron_archive is None:
        electron_archive=output/runtime['archive'];urllib.request.urlretrieve(runtime['url'],electron_archive)
    if sha(electron_archive)!=runtime['archiveSha256']:raise ValueError('Electron archive digest mismatch')
    desktop=build_desktop(root,output/'desktop',Path(result['identity']),electron_archive)
    return {**result,'desktopBundle':desktop['bundle']}

def main():
    parser=argparse.ArgumentParser(allow_abbrev=False)
    for name in ('root','output','electron-archive'):parser.add_argument('--'+name,type=Path,required=name!='electron-archive')
    for name in ('source-sha','source-tree-sha','partner-sha','workflow-sha'):parser.add_argument('--'+name,required=True)
    args=parser.parse_args()
    try:
        result=prepare(args.root,args.output,source_sha=args.source_sha,source_tree_sha=args.source_tree_sha,partner_sha=args.partner_sha,workflow_sha=args.workflow_sha,electron_archive=args.electron_archive)
        (args.output/'application-inputs.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(result))
    except (ValueError,OSError,subprocess.CalledProcessError) as error:parser.exit(1,str(error)+'\n')
if __name__=='__main__':main()
