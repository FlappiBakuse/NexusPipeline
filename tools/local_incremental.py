"""Incremental local assembly using the production builders and verified components."""
from __future__ import annotations
import json
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile

from build_source import source_entries, git
from local_acceptance import freeze
from local_build_cache import ComponentCache, digest, guard, inventory


def select(files, *prefixes):
    return [[f['path'], f['sha256']] for f in files if any(f['path'] == p or f['path'].startswith(p + '/') for p in prefixes)]


def toolchain():
    return [sys.version, sys.platform, os.environ.get('PROCESSOR_ARCHITECTURE'),
            *[subprocess.check_output(command, text=True).strip() for command in
              [['dotnet', '--version'], ['dotnet', '--list-runtimes'], ['node', '--version'], ['npm.cmd', '--version']]],
            sorted((k, v) for k, v in os.environ.items() if k.lower().startswith('npm_config_') and k.lower() != 'npm_config_cache')]


def build(host: Path, plugins: Path, output: Path, cache: Path):
    started = time.time()
    for path in (output, cache):
        guard(path)
        if path.is_relative_to(host) or path.is_relative_to(plugins) or host.is_relative_to(path) or plugins.is_relative_to(path):
            raise ValueError('Build outputs must be external to source')
    owner = {'producer': 'local_incremental.py', 'host': str(host), 'plugins': str(plugins)}
    output.mkdir(parents=True, exist_ok=True)
    marker = output/'local-build-owner.json'
    if marker.exists():
        if json.loads(marker.read_bytes()) != owner: raise ValueError('Unknown build owner')
    elif any(output.iterdir()): raise ValueError('Unknown build output')
    else: marker.write_text(json.dumps(owner), encoding='utf-8')
    lock = output/'build.lock'
    lock.open('x').close()
    events = []
    receipt = {'schemaVersion': 1, 'mode': 'local-acceptance', 'assemblyOnly': True,
               'producer': 'local', 'publishable': False, 'status': 'FAIL', 'humanAcceptance': 'PENDING',
               'startedAt': started, 'components': events}
    logs = output/'logs'; logs.mkdir(exist_ok=True)
    attempt = len(list(logs.glob('attempt-*.json'))) + 1
    try:
        temporary = output/'tmp'; temporary.mkdir(exist_ok=True)
        cache.mkdir(parents=True, exist_ok=True)
        os.environ.update(TEMP=str(temporary), TMP=str(temporary), NUGET_PACKAGES=str(cache/'nuget'),
            NUGET_HTTP_CACHE_PATH=str(cache/'nuget-http'), NUGET_PLUGINS_CACHE_PATH=str(cache/'nuget-plugins'),
            npm_config_cache=str(cache/'npm'), DOTNET_CLI_HOME=str(cache/'dotnet'), DOTNET_ADD_GLOBAL_TO_PATH='false',
            DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true',
            ELECTRON_SKIP_BINARY_DOWNLOAD='1', PYTHONDONTWRITEBYTECODE='1', PYTHONUTF8='1')
        source=output/'source/Host'; partner=output/'source/Plugins'
        hi=freeze(host,source,output/'host-source.json',output/'objects/host')
        pi=freeze(plugins,partner,output/'plugins-source.json',output/'objects/plugins')
        receipt.update(host=hi,plugins=pi)
        cc=ComponentCache(cache/'local-components',events)
        tc=toolchain()
        hf,pf=hi['files'],pi['files']
        rules=select(hf,'tools')
        def command(args,cwd):
            print(' '.join(map(str,args)),flush=True)
            subprocess.run(list(map(str,args)),cwd=cwd,check=True)
        def frontend(target):
            for args in (['npm.cmd','ci','--no-audit','--no-fund'],['npm.cmd','run','typecheck'],['npm.cmd','run','build']):
                command(args,source/'frontend')
            shutil.copytree(source/'frontend/dist',target)
        frontend_output=cc.prepare('frontend',[select(hf,'frontend'),rules,tc],frontend)
        runtime=json.loads((source/'desktop/electron-runtime.json').read_bytes())
        archive=cache/runtime['archive']
        if not archive.exists():
            staging=archive.with_suffix('.download')
            urllib.request.urlretrieve(runtime['url'],staging)
            if digest(staging)!=runtime['archiveSha256']: raise ValueError('Electron archive mismatch')
            staging.rename(archive)
        if digest(archive)!=runtime['archiveSha256']: raise ValueError('Electron archive mismatch')
        desktop_inputs={'code':[select(hf,'desktop'),rules,tc],
            'runtime':[runtime,select(hf,'desktop/runtime-profile.json','desktop/runtime-files.json','src/NexusPipeline.ico'),rules]}
        import application_build, application_payload, application_reader, host_release, pe_manifest
        version=host_release.project_version(source)
        # The full source identity is embedded in Host and maintenance tools, but not in reusable component code.
        def application(target):
            target.mkdir()
            dist=source/'frontend/dist'
            if dist.exists():
                guard(dist)
                shutil.rmtree(dist)
            shutil.copytree(frontend_output,dist)
            inputs=application_build.prepare(source,target/'inputs',source_sha=hi['baseSourceCommit'],source_tree_sha=hi['sourceTreeSha'],
                partner_sha=pi['baseSourceCommit'],workflow_sha=hi['baseSourceCommit'],electron_archive=archive,
                component_cache=cc,cache_inputs=desktop_inputs)
            production=target/'software';production.mkdir()
            bundled=host_release.stage_bundled_plugins(production,partner,source/'src/Modules/Plugins/Repository/BundledPlugins.json')
            bundled_path=target/'BundledPlugins.json';bundled_path.write_text(json.dumps(bundled),encoding='utf-8')
            command(['dotnet','publish','src/NexusPipeline.csproj','-c','Release','-r','win-x64','--self-contained','false',
                '-p:PublishSingleFile=true','-p:DebugType=none','-p:DebugSymbols=false','-p:UseSharedCompilation=false','--disable-build-servers',
                '-p:NexusTestHost=false','-p:NexusFrontendProps='+inputs['frontendProps'],'-p:NexusBuildIdentityPath='+inputs['identity'],
                '-p:NexusUpdaterRecoveryPath='+inputs['updater'],'-p:NexusBundledPluginsManifest='+str(bundled_path),'-o',production],source)
            for file,level in ((production/'NexusPipeline.exe','requireAdministrator'),(Path(inputs['updater']),'requireAdministrator'),
                               (Path(inputs['uninstaller']),'asInvoker'),(Path(inputs['metadataHelper']),'asInvoker')):
                pe_manifest.verify_embedded_manifest(file,level)
            shutil.copy2(inputs['updater'],production/'NexusPipeline.update.exe')
            shutil.copytree(inputs['desktopBundle'],production/'resources/desktop')
            shutil.copy2(source/'README.md',production/'README.md')
            identity=json.loads(Path(inputs['identity']).read_bytes())
            manifest=application_payload.create(production,identity,source)
            application_payload.validate(production,manifest,identity,source)
            if application_reader.read(production,version)!=identity: raise ValueError('Application identity mismatch')
            (target/'identity.json').write_text(json.dumps(identity),encoding='utf-8')
            shutil.rmtree(target/'inputs')
        application_output=cc.prepare('application',[hi['sourceManifestSha256'],hi['baseSourceCommit'],pi['baseSourceCommit'],
            select(pf,'catalog.json','metadata','packages'),tc],application)
        builds=[]
        shared=select(pf,'tools','contracts','schemas','tests/architecture')
        shared += [[f['path'],f['sha256']] for f in pf if '/' not in f['path'] or
                   f['path'].startswith('plugins/general/') and f['path'].endswith(('/package.json','/package-lock.json','.npmrc'))]
        sdk=select(hf,'src/NexusPipeline.Plugin.Abstractions')
        sdk += [[f['path'],f['sha256']] for f in hf if '/' not in f['path']]
        for file in sorted((partner/'plugins').glob('*/*/plugin.json')):
            m=json.loads(file.read_bytes());artifact=m['artifactName']
            def package(target):
                target.mkdir()
                command([sys.executable,partner/'tools/repo.py','package','--root',partner,'--artifact',artifact,
                         '--output',target/'package','--report',target/'report.json','--host-root',source],partner)
            result=cc.prepare('plugin-'+artifact,[select(pf,file.parent.relative_to(partner).as_posix()),shared,
                sdk if m['kind']=='managed-code' else None,rules,tc],package)
            report=json.loads((result/'report.json').read_bytes())
            builds.append({**report,'package':str(result/'package'/report['fileName'])})
        for root,original in ((host,hi),(plugins,pi)):
            actual=[[n,mode,len(raw),hashlib.sha256(raw).hexdigest()] for n,mode,raw in source_entries(root)]
            expected=[[f['path'],f['mode'],f['sizeBytes'],f['sha256']] for f in original['files']]
            if actual!=expected or git(root,'rev-parse','HEAD')!=original['baseSourceCommit']: raise ValueError('Source changed during build')
            for cmd,field in ((['ls-files','--stage','-z'],'gitIndexSha256'),(['show-ref'],'gitRefsSha256')):
                if hashlib.sha256(subprocess.check_output(['git',*cmd],cwd=root)).hexdigest()!=original[field]: raise ValueError('Git identity changed')
        software=output/'acceptance'
        identity=json.loads((application_output/'identity.json').read_bytes())
        expected=inventory(application_output/'software')
        expected=[f for f in expected if not f[0].startswith('plugins/')]
        for item in builds:
            with zipfile.ZipFile(item['package']) as z:
                for entry in z.infolist():
                    name=host_release._safe_package_entry(entry.filename,inner_plugin=True)
                    raw=z.read(entry)
                    expected.append(['plugins/'+item['artifactName']+'/'+name,len(raw),hashlib.sha256(raw).hexdigest()])
        expected.sort()
        if software.exists():
            # Refuse replacement of any user-owned file or a different product; repeated builds are read-only.
            if json.loads((output/'acceptance-files.json').read_bytes()) != expected:
                raise ValueError('Existing acceptance belongs to different inputs; preserve it before replacement')
            for name,size,sha in expected:
                file=software/name;guard(file)
                if not file.is_file() or file.stat().st_size!=size or digest(file)!=sha:
                    raise ValueError('Existing acceptance differs; preserve it before assembling changed software')
        else:
            staging=output/'acceptance.pending'
            if staging.exists(): raise ValueError('Incomplete assembly retained')
            shutil.copytree(application_output/'software',staging)
            shutil.rmtree(staging/'plugins');(staging/'plugins').mkdir()
            for item in builds:
                with zipfile.ZipFile(item['package']) as z:
                    for entry in z.infolist():
                        name=host_release._safe_package_entry(entry.filename,inner_plugin=True)
                        target=staging/'plugins'/item['artifactName']/name;target.parent.mkdir(parents=True,exist_ok=True)
                        target.write_bytes(z.read(entry))
            if inventory(staging)!=expected: raise ValueError('Assembly bytes differ')
            staging.rename(software)
        (output/'acceptance-files.json').write_text(json.dumps(expected),encoding='utf-8')
        receipt.update(status='BUILD_PASS',hostVersion=version,buildId=identity['buildId'],software=str(software),pluginBuilds=builds)
        return receipt
    finally:
        receipt.update(finishedAt=time.time(),elapsedSeconds=time.time()-started)
        data=json.dumps(receipt,ensure_ascii=False,indent=2)
        (output/'local-acceptance-receipt.json').write_text(data,encoding='utf-8')
        (logs/f'attempt-{attempt:02d}.json').write_text(data,encoding='utf-8')
        lock.unlink()
