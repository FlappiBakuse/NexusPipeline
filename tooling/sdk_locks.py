"""Derive locked restore inputs for the tool pack injected by the selected SDK."""
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

from binary_inputs import hash_file
from dotnet_sdk import SDK_PROPS, sdk_key
from source_snapshot import write_json


LINKER = 'Microsoft.NET.ILLink.Tasks'
ENTRY_FIELDS = {'type', 'requested', 'resolved', 'contentHash'}


def linker_version(sdk):
    props = ET.parse(Path(sdk['directory']) / SDK_PROPS)
    versions = [pack.get('ILLinkPackVersion', '') for pack in props.findall('.//KnownILLinkPack')
                if pack.get('Include') == LINKER and pack.get('TargetFramework') == 'net10.0']
    if len(versions) != 1 or re.fullmatch(r'10\.0\.[0-9]+', versions[0]) is None:
        raise ValueError('The resolved SDK does not declare one stable net10.0 ILLink pack')
    return versions[0]


def tool_hash(root, work, cache, sdk, env, version):
    work.mkdir()
    project = work / 'Linker.csproj'
    lock = work / 'packages.lock.json'
    project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                       f'</PropertyGroup><ItemGroup><PackageReference Include="{LINKER}" Version="[{version}]" />'
                       '</ItemGroup></Project>\n', encoding='utf-8', newline='\n')
    command = [sdk['path'], 'restore', str(project), '--configfile', str(root / 'NuGet.Config'), '--nologo',
               '--disable-build-servers', '-m:1', '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false',
               '-p:RestorePackagesWithLockFile=true', f'-p:RestorePackagesPath={cache}', f'-p:NuGetLockFilePath={lock}']
    log = work / 'restore.log'
    with log.open('wb') as stream:
        result = subprocess.run(command, cwd=root, env=env, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise ValueError(f'SDK ILLink restore failed: {log}')
    dependencies = json.loads(lock.read_bytes())['dependencies']
    graph = dependencies.get('net10.0', {})
    entry = graph.get(LINKER, {})
    if (set(dependencies) != {'net10.0'} or set(graph) != {LINKER} or set(entry) != ENTRY_FIELDS or
            entry['type'] != 'Direct' or entry['resolved'] != version or
            re.fullmatch(r'[A-Za-z0-9+/]{86}==', entry['contentHash']) is None):
        raise ValueError('Unexpected dependency graph for the SDK ILLink pack')
    return entry['contentHash']


def linker_entries(document):
    return [graph[LINKER] for graph in document['dependencies'].values() if LINKER in graph]


def prepare_locks(root, projects, run, cache, sdk, env):
    paths = [root / node.attrib['Path'] for node in ET.parse(root / 'NexusPipeline.slnx').findall('Project')]
    baselines = {path.stem + '.packages.lock.json': (path.parent / 'packages.lock.json').read_bytes()
                 for path in paths if path.stem in projects and (path.parent / 'packages.lock.json').is_file()}
    documents = {name: json.loads(data) for name, data in baselines.items()}
    entries = [entry for document in documents.values() for entry in linker_entries(document)]
    if not entries:
        return None
    version = linker_version(sdk)
    if any(set(entry) != ENTRY_FIELDS or entry['type'] != 'Direct' or
           entry['requested'] != f'[{entry["resolved"]}, )' for entry in entries):
        raise ValueError('An application lock has an unexpected ILLink entry')
    if all(entry['resolved'] == version for entry in entries):
        return None
    content_hash = tool_hash(root, run / 'il-link', cache, sdk, env, version)
    directory = run / 'locks' / sdk_key(sdk)
    directory.mkdir(parents=True)
    for name, document in documents.items():
        destination = directory / name
        tool_entries = linker_entries(document)
        if tool_entries:
            for entry in tool_entries:
                entry.update(requested=f'[{version}, )', resolved=version, contentHash=content_hash)
            write_json(destination, document)
        else:
            destination.write_bytes(baselines[name])
    return {'directory': str(directory), 'files': {name: hash_file(directory / name) for name in baselines}}


def lock_arguments(locks):
    if locks is None:
        return []
    directory = Path(locks['directory'])
    files = {path.name: hash_file(path) for path in directory.iterdir() if path.is_file()}
    if files != locks['files']:
        raise ValueError('Derived SDK restore locks changed')
    return [f'-p:NexusLockOutputRoot={directory}']
